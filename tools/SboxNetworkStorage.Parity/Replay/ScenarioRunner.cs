using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SboxNetworkStorage.Parity.Corpus;
using SboxNetworkStorage.Parity.Normalization;
using SboxNetworkStorage.Parity.Results;

namespace SboxNetworkStorage.Parity.Replay;

/// <summary>Replays the corpus in order against one server and records normalized responses.</summary>
public sealed class ScenarioRunner(HttpClient http, Uri target, TemplateContext context, TextWriter log)
{
    public async Task<RunResults> RunAsync(IReadOnlyList<CorpusFile> corpus, CancellationToken ct)
    {
        var results = new RunResults
        {
            Target = target.ToString(),
            ServerVersion = await TryReadServerVersionAsync(ct),
        };

        foreach (var file in corpus)
        {
            foreach (var scenario in file.Scenarios)
            {
                log.WriteLine($"{scenario.Name} ({file.FileName})");
                var scenarioResult = new ScenarioResult { Name = scenario.Name, File = file.FileName };
                foreach (var step in scenario.Steps)
                {
                    var stepResult = await RunStepAsync(step, ct);
                    scenarioResult.Steps.Add(stepResult);
                    var status = stepResult.Response?.Status.ToString() ?? "ERR";
                    log.WriteLine($"  [{status}] {step.Id}  {step.Method} {step.Path}");
                    if (stepResult.Error is not null)
                    {
                        log.WriteLine($"        error: {stepResult.Error}");
                    }

                    foreach (var failure in stepResult.ExpectationFailures ?? [])
                    {
                        log.WriteLine($"        expectation: {failure}");
                    }
                }

                results.Scenarios.Add(scenarioResult);
            }
        }

        return results;
    }

    private async Task<StepResult> RunStepAsync(Step step, CancellationToken ct)
    {
        var result = new StepResult
        {
            Id = step.Id,
            Request = new RecordedRequest
            {
                Method = step.Method.ToUpperInvariant(),
                Path = step.Path,
                Query = step.Query,
                Headers = step.Headers,
                Body = RecordedBody(step),
            },
        };

        string body;
        HttpResponseMessage response;
        try
        {
            using var request = BuildRequest(step);
            response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
            body = await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or ParityException && !ct.IsCancellationRequested)
        {
            result.Error = $"{ex.GetType().Name}: {ex.Message}";
            result.ExpectationFailures = ["request failed before a response was received"];
            return result;
        }

        using (response)
        {
            JsonElement? parsed = TryParse(body);
            Capture(step, parsed);
            var failures = Expectations.Evaluate(step.Expect, (int)response.StatusCode, parsed, context);
            result.ExpectationFailures = failures.Count == 0 ? null : failures;

            var headers = response.Headers.Concat(response.Content.Headers)
                .Select(h => KeyValuePair.Create(h.Key, h.Value));
            result.Response = ResponseNormalizer.Normalize(
                (int)response.StatusCode,
                response.Content.Headers.ContentType?.ToString(),
                headers,
                body,
                Replacements(),
                step.OrderInsensitivePaths,
                step.MaskPaths);
        }

        return result;
    }

    private HttpRequestMessage BuildRequest(Step step)
    {
        var url = new StringBuilder(target.GetLeftPart(UriPartial.Authority)).Append(context.Expand(step.Path));
        if (step.Query is { Count: > 0 })
        {
            url.Append('?').AppendJoin('&', step.Query.Select(q =>
                $"{Uri.EscapeDataString(q.Key)}={Uri.EscapeDataString(context.Expand(q.Value))}"));
        }

        var request = new HttpRequestMessage(new HttpMethod(step.Method.ToUpperInvariant()), url.ToString());
        string? contentType = step.ContentType;
        foreach (var (name, value) in step.Headers ?? [])
        {
            if (name.Equals("content-type", StringComparison.OrdinalIgnoreCase))
            {
                contentType = context.Expand(value);
                continue;
            }

            request.Headers.TryAddWithoutValidation(name, context.Expand(value));
        }

        var payload = step switch
        {
            { Body: { } json } => context.Expand(json)?.ToJsonString() ?? "null",
            { RawBody: { } raw } => context.Expand(raw),
            { GeneratedBody: { } generated } => GenerateBody(generated),
            _ => null,
        };
        if (payload is not null)
        {
            var content = new ByteArrayContent(Encoding.UTF8.GetBytes(payload));
            content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType ?? "application/json");
            request.Content = content;
        }

        return request;
    }

    private static string GenerateBody(GeneratedBody generated)
    {
        var padding = new string('x', Math.Max(0, generated.Bytes - generated.Field.Length - 8));
        return new JsonObject { [generated.Field] = padding }.ToJsonString();
    }

    private static JsonNode? RecordedBody(Step step) => step switch
    {
        { Body: { } json } => JsonNode.Parse(json.GetRawText()),
        { RawBody: { } raw } => new JsonObject { ["$raw"] = raw },
        { GeneratedBody: { } generated } => new JsonObject { ["$generated"] = $"{generated.Bytes} bytes in '{generated.Field}'" },
        _ => null,
    };

    private void Capture(Step step, JsonElement? parsed)
    {
        foreach (var (name, path) in step.Capture ?? [])
        {
            var value = parsed is { } root ? JsonPath.Parse(path).Select(root).FirstOrDefault() : default;
            switch (value.ValueKind)
            {
                case JsonValueKind.String:
                    context.SetCapture(name, value.GetString()!);
                    break;
                case JsonValueKind.Number:
                    context.SetCapture(name, value.GetRawText());
                    break;
                default:
                    log.WriteLine($"        capture '{name}' found nothing at {path}");
                    break;
            }
        }
    }

    private List<KeyValuePair<string, string>> Replacements()
    {
        var replacements = new List<KeyValuePair<string, string>>
        {
            new(context.SecretKey, "${secretKey}"),
            new(context.PublicKey, "${publicKey}"),
            new(context.ProjectId, "${projectId}"),
        };
        replacements.AddRange(context.CaptureReplacements);
        return replacements;
    }

    private async Task<string?> TryReadServerVersionAsync(CancellationToken ct)
    {
        try
        {
            using var response = await http.GetAsync(new Uri(target, "/v3/server-info"), ct);
            var info = TryParse(await response.Content.ReadAsStringAsync(ct));
            return info is { ValueKind: JsonValueKind.Object } root && root.TryGetProperty("version", out var version)
                ? version.ToString()
                : null;
        }
        catch (HttpRequestException ex)
        {
            throw new ParityException($"cannot reach {target}: {ex.Message}");
        }
    }

    private static JsonElement? TryParse(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            return Json.Parse(body);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
