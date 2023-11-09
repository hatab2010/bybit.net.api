﻿using Newtonsoft.Json;
using System.Globalization;
using System.Text;
using System.Web;

namespace bybit.net.api
{
    /// <summary>
    /// Bybit base class for REST sections of the API.
    /// </summary>
    public abstract class BybitService
    {
        private static readonly string UserAgent = "bybit.net.api/" + VersionInfo.GetVersion;
        //private static readonly string CurrentTimeStamp = DateTimeOffset.Now.ToUnixTimeMilliseconds().ToString();
        private readonly string? apiKey;
        private readonly string? apiSecret;
        private readonly bool? useTestnet;
        private readonly HttpClient httpClient;

        public BybitService(HttpClient httpClient, string? apiKey = null, string? apiSecret = null, bool? useTestnet = null)
        {
            this.httpClient = httpClient;
            this.apiKey = apiKey;
            this.apiSecret = apiSecret;
            this.useTestnet = useTestnet ?? false;
        }

        public BybitService(HttpClient httpClient, bool? useTestnet = null)
        {
            this.httpClient = httpClient;
            this.useTestnet = useTestnet ?? false;
        }

        #region public exposed methods
        /// <summary>
        /// Sends an asynchronous public request to Bybit API.
        /// </summary>
        /// <typeparam name="T">The type of object to deserialize the response into.</typeparam>
        /// <param name="requestUri">The URI of the endpoint to request.</param>
        /// <param name="httpMethod">The HTTP method (GET, POST, etc.) of the request.</param>
        /// <param name="query">Optional dictionary containing query parameters for the request.</param>
        /// <returns>A task that represents the asynchronous operation. The task result contains the deserialized response object of type T.</returns>
        protected async Task<T?> SendPublicAsync<T>(string requestUri, HttpMethod httpMethod, Dictionary<string, object>? query = null)
        {
            var currentTimeStamp = DateTimeOffset.Now.ToUnixTimeMilliseconds().ToString();
            string? content = null;
            if (query is not null)
            {
                StringBuilder queryStringBuilder = BuildQueryString(query, new StringBuilder());
                if (httpMethod == HttpMethod.Get)
                {
                    requestUri = queryStringBuilder.Length > 0 ? requestUri + "?" + queryStringBuilder.ToString() : requestUri;
                }
                else if (httpMethod == HttpMethod.Post)
                {
                    content = JsonConvert.SerializeObject(query);
                }
            }
            return await SendAsync<T>(requestUri, httpMethod, currentTimeStamp, null, content);
        }

        /// <summary>
        /// Sends an asynchronous signed request to Bybit API, including authentication.
        /// </summary>
        /// <typeparam name="T">The type of object to deserialize the response into.</typeparam>
        /// <param name="requestUri">The URI of the endpoint to request.</param>
        /// <param name="httpMethod">The HTTP method (GET, POST, etc.) of the request.</param>
        /// <param name="query">Optional dictionary containing query parameters for the request.</param>
        /// <returns>A task that represents the asynchronous operation. The task result contains the deserialized response object of type T.</returns>
        protected async Task<T?> SendSignedAsync<T>(string requestUri, HttpMethod httpMethod, Dictionary<string, object>? query = null)
        {
            StringBuilder queryStringBuilder = new();
            var currentTimeStamp = DateTimeOffset.Now.ToUnixTimeMilliseconds().ToString();

            if (query is not null)
            {
                queryStringBuilder = BuildQueryString(query, queryStringBuilder);
            }

            string? signature = null, content = null;
            IBybitSignatureService bybitSignatureService = new BybitHmacSignatureGenerator(apiKey, apiSecret, currentTimeStamp, BybitConstants.DEFAULT_REC_WINDOW);
            if (httpMethod == HttpMethod.Get)
            {
                requestUri = queryStringBuilder.Length > 0 ? requestUri + "?" + queryStringBuilder.ToString() : requestUri;
                signature = bybitSignatureService.GenerateGetSignature(query ?? new Dictionary<string, object>());
            }
            else if (httpMethod == HttpMethod.Post)
            {
                content = JsonConvert.SerializeObject(query);
                signature = bybitSignatureService.GeneratePostSignature(query ?? new Dictionary<string, object>());
            }

            return await SendAsync<T>(requestUri, httpMethod, currentTimeStamp, signature, content ?? null);
        }
        #endregion

        #region Private Helpers Method
        /// <summary>
        /// Builds a query string for a request from the given set of query parameters.
        /// </summary>
        /// <param name="queryParameters">Dictionary containing the query parameters for the request.</param>
        /// <param name="builder">StringBuilder to which the query string will be appended.</param>
        /// <returns>A StringBuilder containing the constructed query string.</returns>
        private StringBuilder BuildQueryString(Dictionary<string, object> queryParameters, StringBuilder builder)
        {
            IEnumerable<(KeyValuePair<string, object> queryParameter, string queryParameterValue)> enumerable()
            {
                foreach (KeyValuePair<string, object> queryParameter in queryParameters)
                {
                    var queryParameterValue = Convert.ToString(queryParameter.Value, CultureInfo.InvariantCulture);
                    if (!string.IsNullOrWhiteSpace(queryParameterValue))
                    {
                        yield return (queryParameter, queryParameterValue);
                    }
                }
            }

            foreach (var (queryParameter, queryParameterValue) in enumerable())
            {
                if (builder.Length > 0)
                {
                    builder.Append('&');
                }

                builder
                    .Append(queryParameter.Key)
                    .Append('=')
                    .Append(HttpUtility.UrlEncode(queryParameterValue));
            }

            return builder;
        }

        /// <summary>
        /// Sends an asynchronous request to the given URI and returns the response after deserializing it.
        /// </summary>
        /// <typeparam name="T">The type of object to deserialize the response into.</typeparam>
        /// <param name="requestUri">The URI of the endpoint to request.</param>
        /// <param name="httpMethod">The HTTP method (GET, POST, etc.) of the request.</param>
        /// <param name="signature">Optional signature for authentication.</param>
        /// <param name="content">Optional content to include in the request body.</param>
        /// <returns>A task that represents the asynchronous operation. The task result contains the deserialized response object of type T or throws an exception if there's an issue.</returns>
        private async Task<T?> SendAsync<T>(string requestUri, HttpMethod httpMethod, string currentTimeStamp, string? signature = null, string? content = null)
        {
            using HttpRequestMessage request = BuildHttpRequest(requestUri, httpMethod, currentTimeStamp, signature, content);

            HttpResponseMessage response = await this.httpClient.SendAsync(request);

            using HttpContent responseContent = response.Content;
            string contentString = await responseContent.ReadAsStringAsync();
            if (response.IsSuccessStatusCode)
            {
                if (typeof(T) == typeof(string))
                {
                    return (T)(object)contentString;
                }
                else
                {
                    try
                    {
                        return JsonConvert.DeserializeObject<T>(contentString);
                    }
                    catch (JsonReaderException ex)
                    {
                        var clientException = new BybitClientException($"Failed to map server response from '{requestUri}' to given type", -1, ex)
                        {
                            StatusCode = (int)response.StatusCode,
                            Headers = response.Headers.ToDictionary(a => a.Key, a => a.Value)
                        };

                        throw clientException;
                    }
                }
            }
            else
            {
                BybitHttpException? httpException;
                int statusCode = (int)response.StatusCode;
                if (!string.IsNullOrWhiteSpace(contentString))
                {
                    try
                    {
                        httpException = JsonConvert.DeserializeObject<BybitClientException>(contentString);
                    }
                    catch (JsonReaderException ex)
                    {
                        httpException = new BybitClientException(contentString, -1, ex);
                    }
                }
                else
                {
                    httpException = statusCode >= 400 && statusCode < 500 ? new BybitClientException("Unsuccessful response with no content", -1) : new BybitClientException(contentString);
                }

                if (httpException != null) // Check for null before dereferencing
                {
                    httpException.StatusCode = statusCode;
                    httpException.Headers = response.Headers.ToDictionary(a => a.Key, a => a.Value);

                    throw httpException;
                }
            }
            return default;
        }

        /// <summary>
        /// Build http request add attributes to header
        /// </summary>
        /// <param name="requestUri">The URI of the endpoint to request.</param>
        /// <param name="httpMethod">The HTTP method (GET, POST, etc.) of the request.</param>
        /// <param name="signature">Optional signature for authentication.</param>
        /// <param name="content">Optional content to include in the request body.</param>
        /// <returns>Http Request message</returns>
        private HttpRequestMessage BuildHttpRequest(string requestUri, HttpMethod httpMethod, string currentTimeStamp, string? signature, string? content)
        {
            var baseUrl = (this.useTestnet ?? false) ? BybitConstants.HTTP_TESTNET_URL : BybitConstants.HTTP_MAINNET_URL;
            var request = new HttpRequestMessage(httpMethod, baseUrl + requestUri);
            if (signature != null && signature.Length > 0)
            {
                request.Headers.Add("X-BAPI-SIGN", signature);
            }
            request.Headers.Add("User-Agent", UserAgent);
            request.Headers.Add("X-BAPI-SIGN-TYPE", BybitConstants.DEFAULT_SIGN_TYPE);
            request.Headers.Add("X-BAPI-TIMESTAMP", currentTimeStamp);
            request.Headers.Add("X-BAPI-RECV-WINDOW", BybitConstants.DEFAULT_REC_WINDOW);
            if (this.apiKey is not null)
            {
                request.Headers.Add("X-BAPI-API-KEY", this.apiKey);
            }
            if (content is not null)
            {
                request.Content = new StringContent(content, Encoding.UTF8, "application/json");
            }
            return request;
        }
        #endregion
    }
}
