﻿#region Copyright notice and license

// Copyright 2019 The gRPC Authors
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

#endregion

using System.Net;
using System.Net.Http.Headers;
using Greet;
using Grpc.Core;
using Grpc.Net.Client.Internal;
using Grpc.Net.Client.Tests.Infrastructure;
using Grpc.Shared;
using Grpc.Tests.Shared;
using NUnit.Framework;

namespace Grpc.Net.Client.Tests
{
    [TestFixture]
    public class AsyncUnaryCallTests
    {
        [Test]
        public async Task AsyncUnaryCall_Success_HttpRequestMessagePopulated()
        {
            // Arrange
            HttpRequestMessage? httpRequestMessage = null;
            long? requestContentLength = null;

            var httpClient = ClientTestHelpers.CreateTestClient(async request =>
            {
                httpRequestMessage = request;
                requestContentLength = httpRequestMessage!.Content!.Headers!.ContentLength;

                HelloReply reply = new HelloReply
                {
                    Message = "Hello world"
                };

                var streamContent = await ClientTestHelpers.CreateResponseContent(reply).DefaultTimeout();

                return ResponseUtils.CreateResponse(HttpStatusCode.OK, streamContent);
            });
            var invoker = HttpClientCallInvokerFactory.Create(httpClient);

            // Act
            var rs = await invoker.AsyncUnaryCall<HelloRequest, HelloReply>(ClientTestHelpers.ServiceMethod, string.Empty, new CallOptions(), new HelloRequest());

            // Assert
            Assert.AreEqual("Hello world", rs.Message);

            Assert.IsNotNull(httpRequestMessage);
            Assert.AreEqual(new Version(2, 0), httpRequestMessage!.Version);
            Assert.AreEqual(HttpMethod.Post, httpRequestMessage.Method);
            Assert.AreEqual(new Uri("https://localhost/ServiceName/MethodName"), httpRequestMessage.RequestUri);
            Assert.AreEqual(new MediaTypeHeaderValue("application/grpc"), httpRequestMessage.Content?.Headers?.ContentType);
            Assert.AreEqual(GrpcProtocolConstants.TEHeaderValue, httpRequestMessage.Headers.TE.Single().Value);
#if NET6_0_OR_GREATER
            Assert.AreEqual("identity,gzip,deflate", httpRequestMessage.Headers.GetValues(GrpcProtocolConstants.MessageAcceptEncodingHeader).Single());
#else
            Assert.AreEqual("identity,gzip", httpRequestMessage.Headers.GetValues(GrpcProtocolConstants.MessageAcceptEncodingHeader).Single());
#endif
            Assert.AreEqual(null, requestContentLength);

            var grpcVersion = httpRequestMessage.Headers.UserAgent.First();
            Assert.AreEqual("grpc-dotnet", grpcVersion.Product?.Name);
            Assert.IsTrue(!string.IsNullOrEmpty(grpcVersion.Product?.Version));

            // Sanity check that the user agent doesn't have the git hash in it.
            Assert.IsFalse(grpcVersion.Product!.Version!.Contains('+'));
        }

        [Test]
        public async Task AsyncUnaryCall_HasWinHttpHandler_ContentLengthOnHttpRequestMessagePopulated()
        {
            // Arrange
            HttpRequestMessage? httpRequestMessage = null;
            long? requestContentLength = null;

            var handler = TestHttpMessageHandler.Create(async request =>
            {
                httpRequestMessage = request;
                requestContentLength = httpRequestMessage!.Content!.Headers!.ContentLength;

                HelloReply reply = new HelloReply
                {
                    Message = "Hello world"
                };

                var streamContent = await ClientTestHelpers.CreateResponseContent(reply).DefaultTimeout();

                return ResponseUtils.CreateResponse(HttpStatusCode.OK, streamContent);
            });
            // Just need to have a type called WinHttpHandler to activate new behavior.
            var winHttpHandler = new WinHttpHandler(handler);
            var invoker = HttpClientCallInvokerFactory.Create(winHttpHandler, "https://localhost");

            // Act
            var rs = await invoker.AsyncUnaryCall<HelloRequest, HelloReply>(
                ClientTestHelpers.ServiceMethod, string.Empty, new CallOptions(), new HelloRequest { Name = "Hello world" }).ResponseAsync.DefaultTimeout();

            // Assert
            Assert.AreEqual("Hello world", rs.Message);

            Assert.IsNotNull(httpRequestMessage);
            Assert.AreEqual(18, requestContentLength);
        }

        [Test]
        public async Task AsyncUnaryCall_Success_RequestContentSent()
        {
            // Arrange
            HttpContent? content = null;

            var handler = TestHttpMessageHandler.Create(async request =>
            {
                content = request.Content;

                HelloReply reply = new HelloReply
                {
                    Message = "Hello world"
                };

                var streamContent = await ClientTestHelpers.CreateResponseContent(reply).DefaultTimeout();

                return ResponseUtils.CreateResponse(HttpStatusCode.OK, streamContent);
            });
            var invoker = HttpClientCallInvokerFactory.Create(handler, "http://localhost");

            // Act
            var rs = await invoker.AsyncUnaryCall<HelloRequest, HelloReply>(
                ClientTestHelpers.ServiceMethod, string.Empty, new CallOptions(), new HelloRequest { Name = "World" }).ResponseAsync.DefaultTimeout();

            // Assert
            Assert.AreEqual("Hello world", rs.Message);

            Assert.IsNotNull(content);

            var requestContent = await content!.ReadAsStreamAsync().DefaultTimeout();
            var requestMessage = await StreamSerializationHelper.ReadMessageAsync(
                requestContent,
                ClientTestHelpers.ServiceMethod.RequestMarshaller.ContextualDeserializer,
                GrpcProtocolConstants.IdentityGrpcEncoding,
                maximumMessageSize: null,
                GrpcProtocolConstants.DefaultCompressionProviders,
                singleMessage: true,
                CancellationToken.None).DefaultTimeout();

            Assert.AreEqual("World", requestMessage!.Name);
        }

        [Test]
        public async Task AsyncUnaryCall_NonOkStatusTrailer_AccessResponse_ThrowRpcError()
        {
            // Arrange
            var httpClient = ClientTestHelpers.CreateTestClient(request =>
            {
                var response = ResponseUtils.CreateResponse(HttpStatusCode.OK, new ByteArrayContent(Array.Empty<byte>()), StatusCode.Unimplemented);
                return Task.FromResult(response);
            });
            var invoker = HttpClientCallInvokerFactory.Create(httpClient);

            // Act
            var ex = await ExceptionAssert.ThrowsAsync<RpcException>(() => invoker.AsyncUnaryCall<HelloRequest, HelloReply>(ClientTestHelpers.ServiceMethod, string.Empty, new CallOptions(), new HelloRequest()).ResponseAsync).DefaultTimeout();

            // Assert
            Assert.AreEqual(StatusCode.Unimplemented, ex.StatusCode);
        }

        [Test]
        public async Task AsyncUnaryCall_NonOkStatusTrailer_AccessHeaders_ReturnHeaders()
        {
            // Arrange
            var httpClient = ClientTestHelpers.CreateTestClient(request =>
            {
                var response = ResponseUtils.CreateHeadersOnlyResponse(HttpStatusCode.OK, StatusCode.Unimplemented, customHeaders: new Dictionary<string, string> { ["custom"] = "true" });
                return Task.FromResult(response);
            });
            var invoker = HttpClientCallInvokerFactory.Create(httpClient);

            // Act
            var headers = await invoker.AsyncUnaryCall<HelloRequest, HelloReply>(ClientTestHelpers.ServiceMethod, string.Empty, new CallOptions(), new HelloRequest()).ResponseHeadersAsync.DefaultTimeout();

            // Assert
            Assert.AreEqual("true", headers.GetValue("custom"));
        }

        [Test]
        public async Task AsyncUnaryCall_SuccessTrailersOnly_ThrowNoMessageError()
        {
            // Arrange
            HttpResponseMessage? responseMessage = null;
            var httpClient = ClientTestHelpers.CreateTestClient(request =>
            {
                responseMessage = ResponseUtils.CreateHeadersOnlyResponse(HttpStatusCode.OK, StatusCode.OK, customHeaders: new Dictionary<string, string> { [GrpcProtocolConstants.MessageTrailer] = "Detail!" });
                return Task.FromResult(responseMessage);
            });
            var invoker = HttpClientCallInvokerFactory.Create(httpClient);

            // Act
            var call = invoker.AsyncUnaryCall<HelloRequest, HelloReply>(ClientTestHelpers.ServiceMethod, string.Empty, new CallOptions(), new HelloRequest());
            var headers = await call.ResponseHeadersAsync.DefaultTimeout();
            var ex = await ExceptionAssert.ThrowsAsync<RpcException>(() => call.ResponseAsync).DefaultTimeout();

            // Assert
            Assert.NotNull(responseMessage);

            Assert.IsFalse(responseMessage!.TrailingHeaders().Any()); // sanity check that there are no trailers

            Assert.AreEqual(StatusCode.Internal, ex.Status.StatusCode);
            Assert.AreEqual("Failed to deserialize response message.", ex.Status.Detail);

            Assert.AreEqual(StatusCode.Internal, call.GetStatus().StatusCode);
            Assert.AreEqual("Failed to deserialize response message.", call.GetStatus().Detail);

            Assert.AreEqual(0, headers.Count);
            Assert.AreEqual(0, call.GetTrailers().Count);
        }
    }
}
