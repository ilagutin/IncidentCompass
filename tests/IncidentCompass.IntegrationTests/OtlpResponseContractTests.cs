using System.Net;
using Google.Protobuf;
using Microsoft.AspNetCore.Mvc.Testing;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Collector.Trace.V1;

namespace IncidentCompass.IntegrationTests;

/// <summary>
/// the OTLP endpoints return an RPC response body, not a file download. These tests pin
/// the wire contract an OTLP exporter sees - 200 with an <c>application/x-protobuf</c> body that
/// parses as the matching export response, and no file-download or range headers. The exports are
/// empty so the assertion is about the response contract alone and no signal is ingested.
/// </summary>
[Collection(PostgresRepositoryCollection.CollectionName)]
public sealed class OtlpResponseContractTests(PostgresRepositoryFixture postgres)
{
    [DockerAvailableFact]
    public async Task OtlpTraceExport_ReturnsParsableProtobufResponseBody()
    {
        using var scope = await CreateScopeAsync();

        using var response = await PostAsync(
            scope.Client, "/v1/traces", new ExportTraceServiceRequest().ToByteArray());

        var body = await AssertProtobufResponseAsync(response);
        Assert.Null(ExportTraceServiceResponse.Parser.ParseFrom(body).PartialSuccess);
    }

    [DockerAvailableFact]
    public async Task OtlpLogExport_ReturnsParsableProtobufResponseBody()
    {
        using var scope = await CreateScopeAsync();

        using var response = await PostAsync(
            scope.Client, "/v1/logs", new ExportLogsServiceRequest().ToByteArray());

        var body = await AssertProtobufResponseAsync(response);
        Assert.Null(ExportLogsServiceResponse.Parser.ParseFrom(body).PartialSuccess);
    }

    private static async Task<byte[]> AssertProtobufResponseAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/x-protobuf", response.Content.Headers.ContentType?.MediaType);
        Assert.Null(response.Content.Headers.ContentDisposition);
        Assert.Empty(response.Headers.AcceptRanges);
        var body = await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        Assert.Empty(body);
        return body;
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string route, byte[] payload)
    {
        using var content = new ByteArrayContent(payload);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-protobuf");
        return await client.PostAsync(route, content, TestContext.Current.CancellationToken);
    }

    private async Task<TestScope> CreateScopeAsync()
    {
        var connectionString = await postgres.GetConnectionStringAsync();
        await PostgresSchemaTestHelper.EnsureSchemaAsync(connectionString);

        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:IncidentCompass", connectionString);
            builder.UseExplicitMockProviders();
        });

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });

        return new TestScope(factory, client);
    }

    private sealed record TestScope(WebApplicationFactory<Program> Factory, HttpClient Client) : IDisposable
    {
        public void Dispose()
        {
            Client.Dispose();
            Factory.Dispose();
        }
    }
}
