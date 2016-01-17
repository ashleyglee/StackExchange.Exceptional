using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Data.SqlClient;
using System.Linq;
using System.Runtime.Serialization;
using System.Text.RegularExpressions;
using Jil;
using StackExchange.Exceptional.Extensions;
#if COREFX
using Microsoft.AspNet.Http;
using Microsoft.Extensions.Primitives;
#else
using System.Diagnostics;
using System.Web;
#endif

namespace StackExchange.Exceptional
{
    /// <summary>
    /// Represents a logical application error (as opposed to the actual exception it may be representing).
    /// </summary>
    public class Error
    {
        internal const string CollectionErrorKey = "CollectionFetchError";

        /// <summary>
        /// Filters on form values *not * to log, because they contain sensitive data
        /// </summary>
        public static ConcurrentDictionary<string, string> FormLogFilters { get; }

        /// <summary>
        /// Filters on form values *not * to log, because they contain sensitive data
        /// </summary>
        public static ConcurrentDictionary<string, string> CookieLogFilters { get; }
        
        /// <summary>
        /// Gets the data include pattern, like "SQL.*|Redis-*" to match against .Data keys to include when logging
        /// </summary>
        public static Regex DataIncludeRegex { get; set; }

        static Error()
        {
            CookieLogFilters = new ConcurrentDictionary<string, string>();
            Settings.Current.LogFilters.CookieFilters.All().ForEach(flf => CookieLogFilters[flf.Name] = flf.ReplaceWith ?? "");

            FormLogFilters = new ConcurrentDictionary<string, string>();
            Settings.Current.LogFilters.FormFilters.All().ForEach(flf => FormLogFilters[flf.Name] = flf.ReplaceWith ?? "");

            if (!string.IsNullOrEmpty(Settings.Current.DataIncludePattern))
            {
                DataIncludeRegex = new Regex(Settings.Current.DataIncludePattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
            }
        }

        /// <summary>
        /// The Id on this error, strictly for primary keying on persistent stores
        /// </summary>
        [IgnoreDataMember]
        public long Id { get; set; }

        /// <summary>
        /// Unique identifier for this error, gernated on the server it came from
        /// </summary>
        public Guid GUID { get; set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="Error"/> class.
        /// </summary>
        public Error() { }

        /// <summary>
        /// Initializes a new instance of the <see cref="Error"/> class from a given <see cref="Exception"/> instance.
        /// </summary>
        public Error(Exception e): this(e, null) { }

        /// <summary>
        /// Initializes a new instance of the <see cref="Error"/> class
        /// from a given <see cref="Exception"/> instance and 
        /// <see cref="Microsoft.AspNet.Http.HttpContext"/> instance representing the HTTP 
        /// context during the exception.
        /// </summary>
        public Error(Exception e, HttpContext context, string applicationName = null)
        {
            if (e == null) throw new ArgumentNullException(nameof(e));

            Exception = e;
            var baseException = e;

            // if it's not a .Net core exception, usually more information is being added
            // so use the wrapper for the message, type, etc.
            // if it's a .Net core exception type, drill down and get the innermost exception
            if (IsBuiltInException(e))
                baseException = e.GetBaseException();

            GUID = Guid.NewGuid();
            ApplicationName = applicationName ?? ErrorStore.ApplicationName;
#if COREFX
            // TODO: This only really works on Windows! - fixing in RC2: https://github.com/dotnet/corefx/issues/4306
            MachineName = Environment.GetEnvironmentVariable("HOSTNAME");
#else
            MachineName = Environment.MachineName;
#endif
            Type = baseException.GetType().FullName;
            Message = baseException.Message;
            Source = baseException.Source;
            Detail = e.ToString();
            CreationDate = DateTime.UtcNow;
            DuplicateCount = 1;

            SetContextProperties(context);

            ErrorHash = GetHash();
        }

        /// <summary>
        /// Sets Error properties pulled from HttpContext, if present
        /// </summary>
        /// <param name="context">The HttpContext related to the request</param>
        private void SetContextProperties(HttpContext context)
        {
            if (context == null) return;

            var request = context.Request;
            StatusCode = context.Response.StatusCode;

#if COREFX
            Func<IEnumerable<KeyValuePair<string, StringValues>>, NameValueCollection> tryGetCollection = col =>
            {
                var nvc = new NameValueCollection();
                if (col == null) return nvc;
                foreach (var i in col)
                {
                    foreach (var v in i.Value)
                    {
                        nvc.Add(i.Key, v);
                    }
                }
                return nvc;
            };

            Action<NameValueCollection, ConcurrentDictionary<string, string>> mask = (col, lookup) =>
            {
                if (lookup.Count <= 0) return;
                foreach (var k in lookup.Keys)
                {
                    if (col[k] != null)
                        col[k] = lookup[k];
                }
            };

            // TODO: Generate ServerVairables
            //ServerVariables = tryGetCollection(r => r.ServerVariables);
            QueryString = tryGetCollection(request.Query);
            Form = tryGetCollection(request.Form);
            mask(Form, FormLogFilters);
            Cookies = tryGetCollection(request.Cookies);
            mask(Cookies, CookieLogFilters);

            RequestHeaders = tryGetCollection(
                request.Headers.Where(i => string.Compare(i.Key, "Cookie", StringComparison.OrdinalIgnoreCase) != 0));
#else
            Func<Func<HttpRequest, NameValueCollection>, NameValueCollection> tryGetCollection = getter =>
                {
                    try
                    {
                        return new NameValueCollection(getter(request));
                    }
                    catch (Exception e)
                    {
                        Debug.WriteLine("Error parsing collection: " + e.Message);
                        return new NameValueCollection {{CollectionErrorKey, e.Message}};
                    }
                };

            ServerVariables = tryGetCollection(r => r.ServerVariables);
            QueryString = tryGetCollection(r => r.QueryString);
            Form = tryGetCollection(r => r.Form);
            
            // Filter form variables for sensitive information
            if (FormLogFilters.Count > 0)
            {
                foreach (var k in FormLogFilters.Keys)
                {
                    if (Form[k] != null)
                        Form[k] = FormLogFilters[k];
                }
            }

            try
            {
                Cookies = new NameValueCollection(request.Cookies.Count);
                for (var i = 0; i < request.Cookies.Count; i++)
                {
                    var name = request.Cookies[i].Name;
                    string val;
                    CookieLogFilters.TryGetValue(name, out val);
                    Cookies.Add(name, val ?? request.Cookies[i].Value);
                }
            }
            catch (Exception e)
            {
                Debug.WriteLine("Error parsing cookie collection: " + e.Message);
            }

            RequestHeaders = new NameValueCollection(request.Headers.Count);
            foreach(var header in request.Headers.AllKeys)
            {
                // Cookies are handled above, no need to repeat
                if (string.Compare(header, "Cookie", StringComparison.OrdinalIgnoreCase) == 0)
                    continue;

                if (request.Headers[header] != null)
                    RequestHeaders[header] = request.Headers[header];
            }
#endif
        }

        internal void AddFromData(Exception exception)
        {
            if (exception.Data == null) return;

            // Historical special case
                if (exception.Data.Contains("SQL"))
                SQL = exception.Data["SQL"] as string;

            var se = exception as SqlException;
            if (se != null)
            {
                if (CustomData == null)
                    CustomData = new Dictionary<string, string>();

                CustomData["SQL-Server"] = se.Server;
                CustomData["SQL-ErrorNumber"] = se.Number.ToString();
                CustomData["SQL-LineNumber"] = se.LineNumber.ToString();
                if (se.Procedure.HasValue())
                {
                    CustomData["SQL-Procedure"] = se.Procedure;
                }
            }
            // Regardless of what Resharper may be telling you, .Data can be null on things like a null ref exception.
            if (DataIncludeRegex != null)
            {
                if (CustomData == null)
                    CustomData = new Dictionary<string, string>();

                foreach (string k in exception.Data.Keys)
                {
                    if (!DataIncludeRegex.IsMatch(k)) continue;
                    CustomData[k] = exception.Data[k] != null ? exception.Data[k].ToString() : "";
                }
            }
        }

        /// <summary>
        /// returns if the type of the exception is built into .Net Full CLR
        /// </summary>
        /// <param name="e">The exception to check</param>
        /// <returns>True if the exception is a type from within the CLR, false if it's a user/third party type</returns>
        private bool IsBuiltInException(Exception e)
        {
#if COREFX
            // TODO: Think more about what this should reflect - meant to cut out noise
            return false;
#else
            return e.GetType().Module.ScopeName == "CommonLanguageRuntimeLibrary";
#endif

        }

        /// <summary>
        /// Gets a unique-enough hash of this error.  Stored as a quick comparison mechanism to rollup duplicate errors.
        /// </summary>
        /// <returns>"Unique" hash for this error</returns>
        public int? GetHash()
        {
            if (!Detail.HasValue()) return null;

            var result = Detail.GetHashCode();
            if (RollupPerServer && MachineName.HasValue())
                result = (result * 397)^ MachineName.GetHashCode();

            return result;
        }

        /// <summary>
        /// Reflects if the error is protected from deletion
        /// </summary>
        public bool IsProtected { get; set; }

        /// <summary>
        /// Gets the <see cref="Exception"/> instance used to create this error
        /// </summary>
        [IgnoreDataMember]
        public Exception Exception { get; set; }

        /// <summary>
        /// Gets the name of the application that threw this exception
        /// </summary>
        public string ApplicationName { get; set; }

        /// <summary>
        /// Gets the hostname of where the exception occured
        /// </summary>
        public string MachineName { get; set; }

        /// <summary>
        /// Get the type of error
        /// </summary>
        public string Type { get; set; }

        /// <summary>
        /// Gets the source of this error
        /// </summary>
        public string Source { get; set; }

        /// <summary>
        /// Gets the exception message
        /// </summary>
        public string Message { get; set; }

        /// <summary>
        /// Gets the detail/stack trace of this error
        /// </summary>
        public string Detail { get; set; }

        /// <summary>
        /// The hash that describes this error
        /// </summary>
        public int? ErrorHash { get; set; }

        /// <summary>
        /// Gets the time in UTC that the error occured
        /// </summary>
        public DateTime CreationDate { get; set; }

        /// <summary>
        /// Gets the HTTP Status code associated with the request
        /// </summary>
        public int? StatusCode { get; set; }

        /// <summary>
        /// Gets the server variables collection for the request
        /// </summary>
        [IgnoreDataMember]
        public NameValueCollection ServerVariables { get; set; }
        
        /// <summary>
        /// Gets the query string collection for the request
        /// </summary>
        [IgnoreDataMember]
        public NameValueCollection QueryString { get; set; }
        
        /// <summary>
        /// Gets the form collection for the request
        /// </summary>
        [IgnoreDataMember]
        public NameValueCollection Form { get; set; }
        
        /// <summary>
        /// Gets a collection representing the client cookies of the request
        /// </summary>
        [IgnoreDataMember]
        public NameValueCollection Cookies { get; set; }

        /// <summary>
        /// Gets a collection representing the headers sent with the request
        /// </summary>
        [IgnoreDataMember]
        public NameValueCollection RequestHeaders { get; set; }

        /// <summary>
        /// Gets a collection of custom data added at log time
        /// </summary>
        public Dictionary<string, string> CustomData { get; set; }
        
        /// <summary>
        /// The number of newer Errors that have been discarded because they match this Error and fall within the configured 
        /// "IgnoreSimilarExceptionsThreshold" TimeSpan value.
        /// </summary>
        public int? DuplicateCount { get; set; }

        /// <summary>
        /// This flag is to indicate that there were no matches of this error in when added to the queue or store.
        /// </summary>
        [IgnoreDataMember]
        public bool IsDuplicate { get; set; }

        /// <summary>
        /// Gets the SQL command text assocaited with this error
        /// </summary>
        public string SQL { get; set; }
        
        /// <summary>
        /// Date this error was deleted (for stores that support deletion and retention, e.g. SQL)
        /// </summary>
        public DateTime? DeletionDate { get; set; }

        /// <summary>
        /// The URL host of the request causing this error
        /// </summary>
        public string Host { get { return _host ?? (_host = ServerVariables == null ? "" : ServerVariables["HTTP_HOST"]); } set { _host = value; } }
        private string _host;

        /// <summary>
        /// The URL path of the request causing this error
        /// </summary>
        public string Url { get { return _url ?? (_url = ServerVariables == null ? "" : ServerVariables["URL"]); } set { _url = value; } }
        private string _url;

        /// <summary>
        /// The HTTP Method causing this error, e.g. GET or POST
        /// </summary>
        public string HTTPMethod { get { return _httpMethod ?? (_httpMethod = ServerVariables == null ? "" : ServerVariables["REQUEST_METHOD"]); } set { _httpMethod = value; } }
        private string _httpMethod;

        /// <summary>
        /// The IPAddress of the request causing this error
        /// </summary>
        public string IPAddress { get { return _ipAddress ?? (_ipAddress = ServerVariables == null ? "" : ServerVariables.GetRemoteIP()); } set { _ipAddress = value; } }
        private string _ipAddress;
        
        /// <summary>
        /// Json populated from database stored, deserialized after if needed
        /// </summary>
        [IgnoreDataMember]
        public string FullJson { get; set; }

        /// <summary>
        /// Whether to roll up errors per-server. E.g. should an identical error happening on 2 separate servers be a DuplicateCount++, or 2 separate errors.
        /// </summary>
        [IgnoreDataMember]
        public bool RollupPerServer { get; set; }

        /// <summary>
        /// Returns the value of the <see cref="Message"/> property.
        /// </summary>
        public override string ToString() => Message;
        
        /// <summary>
        /// Create a copy of the error and collections so if it's modified in memory logging is not affected
        /// </summary>
        /// <returns>A clone of this error</returns>
        public Error Clone()
        {
            var copy = (Error) MemberwiseClone();
            if (ServerVariables != null) copy.ServerVariables = new NameValueCollection(ServerVariables);
            if (QueryString != null) copy.QueryString = new NameValueCollection(QueryString);
            if (Form != null) copy.Form = new NameValueCollection(Form);
            if (Cookies != null) copy.Cookies = new NameValueCollection(Cookies);
            if (RequestHeaders != null) copy.RequestHeaders = new NameValueCollection(RequestHeaders);
            if (CustomData != null) copy.CustomData = new Dictionary<string, string>(CustomData);
            return copy;
        }

        /// <summary>
        /// Variables strictly for JSON serialziation, to maintain non-dictonary behavior
        /// </summary>
        public List<NameValuePair> ServerVariablesSerializable
        {
            get { return GetPairs(ServerVariables); }
            set { ServerVariables = GetNameValueCollection(value); }
        }
        /// <summary>
        /// Variables strictly for JSON serialziation, to maintain non-dictonary behavior
        /// </summary>
        public List<NameValuePair> QueryStringSerializable
        {
            get { return GetPairs(QueryString); }
            set { QueryString = GetNameValueCollection(value); }
        }
        /// <summary>
        /// Variables strictly for JSON serialziation, to maintain non-dictonary behavior
        /// </summary>
        public List<NameValuePair> FormSerializable
        {
            get { return GetPairs(Form); }
            set { Form = GetNameValueCollection(value); }
        }
        /// <summary>
        /// Variables strictly for JSON serialziation, to maintain non-dictonary behavior
        /// </summary>
        public List<NameValuePair> CookiesSerializable
        {
            get { return GetPairs(Cookies); }
            set { Cookies = GetNameValueCollection(value); }
        }

        /// <summary>
        /// Variables strictly for JSON serialziation, to maintain non-dictonary behavior
        /// </summary>
        public List<NameValuePair> RequestHeadersSerializable
        {
            get { return GetPairs(RequestHeaders); }
            set { RequestHeaders = GetNameValueCollection(value); }
        }

        /// <summary>
        /// Gets a JSON representation for this error
        /// </summary>
        public string ToJson() => JSON.Serialize(this);

        /// <summary>
        /// Gets a JSON representation for this error suitable for cross-domain 
        /// </summary>
        /// <returns></returns>
        public string ToDetailedJson()
        {
            return JSON.Serialize(new
            {
                GUID,
                ApplicationName,
                CreationDate = CreationDate.ToEpochTime(),
                CustomData,
                DeletionDate = DeletionDate?.ToEpochTime(),
                Detail,
                DuplicateCount,
                ErrorHash,
                HTTPMethod,
                Host,
                IPAddress,
                IsProtected,
                MachineName,
                Message,
                SQL,
                Source,
                StatusCode,
                Type,
                Url,
                QueryString = ServerVariables?["QUERY_STRING"],
                ServerVariables = ServerVariablesSerializable.ToJsonDictionary(),
                CookieVariables = CookiesSerializable.ToJsonDictionary(),
                RequestHeaders = RequestHeadersSerializable.ToJsonDictionary(),
                QueryStringVariables = QueryStringSerializable.ToJsonDictionary(),
                FormVariables = FormSerializable.ToJsonDictionary()
            });
        }

        /// <summary>
        /// Deserializes provided JSON into an Error object
        /// </summary>
        /// <param name="json">JSON representing an Error</param>
        /// <returns>The Error object</returns>
        public static Error FromJson(string json)
        {
            return JSON.Deserialize<Error>(json);
        }

        /// <summary>
        /// Serialization class in place of the NameValueCollection pairs
        /// </summary>
        /// <remarks>This exists because things like a querystring can havle multiple values, they are not a dictionary</remarks>
        public class NameValuePair
        {
            /// <summary>
            /// The name for this variable
            /// </summary>
            public string Name { get; set; }
            /// <summary>
            /// The value for this variable
            /// </summary>
            public string Value { get; set; }
        }

        private List<NameValuePair> GetPairs(NameValueCollection nvc)
        {
            var result = new List<NameValuePair>();
            if (nvc == null) return null;

            for (int i = 0; i < nvc.Count; i++)
            {
                result.Add(new NameValuePair {Name = nvc.GetKey(i), Value = nvc.Get(i)});
            }
            return result;
        }

        private NameValueCollection GetNameValueCollection(List<NameValuePair> pairs)
        {
            var result = new NameValueCollection();
            if (pairs == null) return null;

            foreach(var p in pairs)
            {
                result.Add(p.Name, p.Value);
            }
            return result;
        }
    }
}