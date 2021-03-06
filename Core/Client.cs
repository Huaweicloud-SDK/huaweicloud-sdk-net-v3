﻿/*
 * Copyright 2020 Huawei Technologies Co.,Ltd.
 * 
 * Licensed to the Apache Software Foundation (ASF) under one
 * or more contributor license agreements.  See the NOTICE file
 * distributed with this work for additional information
 * regarding copyright ownership.  The ASF licenses this file
 * to you under the Apache License, Version 2.0 (the
 * "License"); you may not use this file except in compliance
 * with the License.  You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing,
 * software distributed under the License is distributed on an
 * "AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY
 * KIND, either express or implied.  See the License for the
 * specific language governing permissions and limitations
 * under the License.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using HuaweiCloud.SDK.Core.Auth;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using static System.String;

namespace HuaweiCloud.SDK.Core
{
    public class Client
    {
        public class ClientBuilder<T> where T : Client
        {
            private ICredential _credential;
            private HttpConfig _httpConfig;
            private string _endPoint;

            public ClientBuilder<T> WithCredential(ICredential credential)
            {
                this._credential = credential;
                return this;
            }

            public ClientBuilder<T> WithHttpConfig(HttpConfig httpConfig)
            {
                this._httpConfig = httpConfig;
                return this;
            }

            public ClientBuilder<T> WithEndPoint(string endPoint)
            {
                this._endPoint = endPoint;
                return this;
            }

            public T Build()
            {
                Client client = Activator.CreateInstance<T>();

                if (this._credential == null)
                {
                    this._credential = GetCredentialFromEnvironmentVariables();
                }

                client.WithCredential(this._credential)
                    .WithEndPoint(this._endPoint)
                    .WithHttpConfig(_httpConfig ?? HttpConfig.GetDefaultConfig());
                return (T) client;
            }

            private ICredential GetCredentialFromEnvironmentVariables()
            {
                var projectId = Environment.GetEnvironmentVariable("HUAWEICLOUD_SDK_PROJECT_ID");
                var domainId = Environment.GetEnvironmentVariable("HUAWEICLOUD_SDK_DOMAIN_ID");
                var ak = Environment.GetEnvironmentVariable("HUAWEICLOUD_SDK_AK");
                var sk = Environment.GetEnvironmentVariable("HUAWEICLOUD_SDK_SK");

                return new BasicCredentials(ak, sk, projectId, domainId);
            }
        }

        private ICredential _credential;
        private HttpConfig _httpConfig;
        private string _endpoint;
        private const string XRequestId = "X-Request-Id";
        private const string XRequestAgent = "User-Agent";
        private const string CredentialsNull = "Credentials cannot be null.";

        private Client WithCredential(ICredential credentials)
        {
            this._credential = credentials ?? throw new ArgumentNullException(CredentialsNull);
            this._httpConfig = HttpConfig.GetDefaultConfig();
            return this;
        }

        private Client WithHttpConfig(HttpConfig httpConfig)
        {
            this._httpConfig = httpConfig;
            return this;
        }

        private Client WithEndPoint(string endPoint)
        {
            this._endpoint = endPoint;
            return this;
        }

        protected SdkResponse DoHttpRequest(string methodType, SdkRequest request)
        {
            var url = _endpoint
                      + HttpUtils.AddUrlPath(request.Path, _credential.GetPathParamDictionary())
                      + (IsNullOrEmpty(request.QueryParams) ? "" : "?" + request.QueryParams);
            return _http(url, methodType.ToUpper(), request.ContentType, request.Header, request.Body ?? "");
        }

        private SdkResponse _http(string url, string method, string contentType, Dictionary<string, string> headers,
            string content)
        {
            var request = GetHttpRequest(url, method, contentType, headers, content);
            _credential.SignAuthRequest(request);
            var client = new SdkHttpClient(_httpConfig);
            var message = client.InitHttpRequest(request);
            var result = new SdkResponse();
            try
            {
                var task = client.DoHttpRequest(message);
                var requestId = task.Result.Headers.GetValues(XRequestId).FirstOrDefault();
                result.HttpStatusCode = (int) task.Result.StatusCode;
                result.HttpHeaders = task.Result.Headers.ToString();
                result.HttpBody = task.Result.Content.ReadAsStringAsync().Result;
                if (result.HttpStatusCode < 400)
                {
                    return result;
                }

                SdkError sdkError;
                try
                {
                    sdkError = GetSdkErrorFromResponse(requestId, result);
                }
                catch (Exception exception)
                {
                    throw new ServerResponseException(result.HttpStatusCode,
                        new SdkError {ErrorMsg = exception.Message});
                }

                if (result.HttpStatusCode >= 400 && result.HttpStatusCode < 500)
                {
                    throw new ClientRequestException(result.HttpStatusCode, sdkError);
                }

                throw new ServerResponseException(result.HttpStatusCode, sdkError);
            }
            catch (AggregateException aggregateException)
            {
                throw new ConnectionException(aggregateException.Message);
            }
        }

        protected virtual SdkError HandleServiceSpecException(SdkResponse response)
        {
            return new SdkError();
        }

        private SdkError HandleServiceCommonException(SdkResponse response)
        {
            var exception = JsonConvert.DeserializeObject<Dictionary<string, object>>(response.GetHttpBody());
            if (exception.ContainsKey("code") && exception.ContainsKey("message"))
            {
                return new SdkError(exception["code"].ToString(), exception["message"].ToString());
            }

            foreach (var item in exception)
            {
                var jValue = JObject.Parse(item.Value.ToString());
                var message = jValue["message"];
                var code = jValue["code"];
                if (message != null && code != null)
                {
                    return new SdkError(code.ToString(), message.ToString());
                }
            }

            return new SdkError(response.GetHttpBody());
        }

        private SdkError GetSdkErrorFromResponse(string requestId, SdkResponse response)
        {
            SdkError sdkError;
            try
            {
                sdkError = JsonUtils.DeSerialize<SdkError>(response);
                if (IsNullOrEmpty(sdkError.ErrorCode) || IsNullOrEmpty(sdkError.ErrorMsg))
                {
                    sdkError = HandleServiceCommonException(response);
                }
            }
            catch (Exception)
            {
                sdkError = new SdkError();
            }

            if (IsNullOrEmpty(sdkError.ErrorMsg))
            {
                sdkError = HandleServiceSpecException(response);
            }

            if (IsNullOrEmpty(sdkError.RequestId))
            {
                sdkError.RequestId = requestId;
            }

            return sdkError;
        }

        private HttpRequest GetHttpRequest(string url, string method, string contentType,
            Dictionary<string, string> headers, string content)
        {
            var request = new HttpRequest(method.ToUpper(), contentType, new Uri(url)) {Body = content};
            SetHeaders(request, headers);
            return request;
        }

        private void SetHeaders(HttpRequest request, Dictionary<string, string> headers)
        {
            if (headers != null)
            {
                foreach (var header in headers)
                {
                    request.Headers.Add(header.Key, header.Value);
                }

                request.Headers.Add(XRequestAgent, "huaweicloud-sdk-net/3.0");
            }
        }
    }
}