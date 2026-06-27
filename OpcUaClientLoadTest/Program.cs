using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;

var configPath = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.OrdinalIgnoreCase)) ?? "appsettings.json";
if (args.Any(a => string.Equals(a, "--help", StringComparison.OrdinalIgnoreCase) || string.Equals(a, "-h", StringComparison.OrdinalIgnoreCase)))
{
    PrintUsage();
    return 0;
}

if (args.Any(a => string.Equals(a, "--init-hd-opcua-config", StringComparison.OrdinalIgnoreCase)))
{
    var outputPath = GetArgValue(args, "--out") ?? "appsettings.hd-opcua.json";
    var host = GetArgValue(args, "--host") ?? "127.0.0.1";
    var basePort = int.TryParse(GetArgValue(args, "--base-port"), out var parsedBasePort) ? parsedBasePort : 4840;
    var shards = int.TryParse(GetArgValue(args, "--shards"), out var parsedShards) ? parsedShards : 16;
    var durationSeconds = int.TryParse(GetArgValue(args, "--duration-seconds"), out var parsedDuration) ? parsedDuration : 1800;

    HdOpcUaConfigFactory.Write(outputPath, host, basePort, shards, durationSeconds);
    Console.WriteLine($"HD_OPC_UA client test config written: {Path.GetFullPath(outputPath)}");
    return 0;
}

if (!File.Exists(configPath))
{
    Console.Error.WriteLine($"Config file not found: {Path.GetFullPath(configPath)}");
    Console.Error.WriteLine("Copy appsettings.sample.json to appsettings.json and add your OPC UA endpoints.");
    return 2;
}

var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true
};
var runConfig = JsonSerializer.Deserialize<LoadTestConfig>(File.ReadAllText(configPath), options)
                ?? throw new InvalidOperationException("Invalid config file.");

if (runConfig.Endpoints.Count == 0)
{
    Console.Error.WriteLine("No endpoints configured.");
    return 2;
}

Directory.CreateDirectory(runConfig.OutputDirectory);
var startedAt = DateTimeOffset.Now;
var appConfig = await OpcUaApplication.CreateAsync(runConfig.AcceptUntrustedCertificates);
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(runConfig.DurationSeconds));

Console.WriteLine($"OPC UA client load test started at {startedAt:yyyy-MM-dd HH:mm:ss zzz}");
Console.WriteLine($"Endpoints: {runConfig.Endpoints.Count}; Duration: {runConfig.DurationSeconds}s");

var tasks = runConfig.Endpoints.Select(endpoint => EndpointRunner.RunAsync(endpoint, runConfig, appConfig, cts.Token)).ToArray();
var endpointReports = await Task.WhenAll(tasks);
var finishedAt = DateTimeOffset.Now;

var report = new RunReport
{
    StartedAt = startedAt,
    FinishedAt = finishedAt,
    DurationSeconds = (finishedAt - startedAt).TotalSeconds,
    MachineName = Environment.MachineName,
    Endpoints = endpointReports.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList()
};

var stamp = startedAt.ToString("yyyyMMdd-HHmmss");
var jsonPath = Path.Combine(runConfig.OutputDirectory, $"opcua-loadtest-{stamp}.json");
var endpointCsvPath = Path.Combine(runConfig.OutputDirectory, $"opcua-loadtest-endpoints-{stamp}.csv");
var signalCsvPath = Path.Combine(runConfig.OutputDirectory, $"opcua-loadtest-signals-{stamp}.csv");
var htmlPath = Path.Combine(runConfig.OutputDirectory, $"opcua-loadtest-{stamp}.html");

ReportWriter.WriteJson(jsonPath, report);
ReportWriter.WriteEndpointCsv(endpointCsvPath, report);
ReportWriter.WriteSignalCsv(signalCsvPath, report);
ReportWriter.WriteHtml(htmlPath, report);

Console.WriteLine();
Console.WriteLine("Report files:");
Console.WriteLine($"  HTML : {Path.GetFullPath(htmlPath)}");
Console.WriteLine($"  JSON : {Path.GetFullPath(jsonPath)}");
Console.WriteLine($"  CSV  : {Path.GetFullPath(endpointCsvPath)}");
Console.WriteLine($"  CSV  : {Path.GetFullPath(signalCsvPath)}");
return report.Endpoints.Any(e => e.Error is not null) ? 1 : 0;

static class OpcUaApplication
{
    public static async Task<ApplicationConfiguration> CreateAsync(bool acceptUntrustedCertificates)
    {
        var application = new ApplicationInstance
        {
            ApplicationName = "OPC UA Client Load Test",
            ApplicationType = ApplicationType.Client,
            ConfigSectionName = "OpcUaClientLoadTest"
        };

        var config = new ApplicationConfiguration
        {
            ApplicationName = application.ApplicationName,
            ApplicationUri = $"urn:{Dns.GetHostName()}:opcua-client-loadtest",
            ApplicationType = ApplicationType.Client,
            SecurityConfiguration = new SecurityConfiguration
            {
                ApplicationCertificate = new CertificateIdentifier
                {
                    StoreType = "Directory",
                    StorePath = "pki/own",
                    SubjectName = application.ApplicationName
                },
                TrustedPeerCertificates = new CertificateTrustList
                {
                    StoreType = "Directory",
                    StorePath = "pki/trusted"
                },
                RejectedCertificateStore = new CertificateTrustList
                {
                    StoreType = "Directory",
                    StorePath = "pki/rejected"
                },
                AutoAcceptUntrustedCertificates = acceptUntrustedCertificates,
                RejectSHA1SignedCertificates = false,
                MinimumCertificateKeySize = 2048
            },
            TransportConfigurations = new TransportConfigurationCollection(),
            TransportQuotas = new TransportQuotas
            {
                OperationTimeout = 60000,
                MaxStringLength = 4 * 1024 * 1024,
                MaxByteStringLength = 16 * 1024 * 1024,
                MaxArrayLength = 65535,
                MaxMessageSize = 16 * 1024 * 1024,
                MaxBufferSize = 65535,
                ChannelLifetime = 300000,
                SecurityTokenLifetime = 3600000
            },
            ClientConfiguration = new ClientConfiguration
            {
                DefaultSessionTimeout = 60000,
                MinSubscriptionLifetime = 10000
            },
            DisableHiResClock = false
        };

        await config.Validate(ApplicationType.Client);
        if (config.SecurityConfiguration.AutoAcceptUntrustedCertificates)
        {
            config.CertificateValidator.CertificateValidation += (_, e) => e.Accept = e.Error.StatusCode == StatusCodes.BadCertificateUntrusted;
        }

        application.ApplicationConfiguration = config;
        await application.CheckApplicationInstanceCertificate(false, 2048);
        return config;
    }
}

static class EndpointRunner
{
    public static async Task<EndpointReport> RunAsync(EndpointConfig endpoint, LoadTestConfig runConfig, ApplicationConfiguration appConfig, CancellationToken token)
    {
        var report = new EndpointReport
        {
            Name = endpoint.Name,
            Url = endpoint.Url
        };

        var sw = Stopwatch.StartNew();
        Session? session = null;
        Subscription? subscription = null;
        var signalStats = new ConcurrentDictionary<string, SignalStats>(StringComparer.Ordinal);

        try
        {
            var selectedEndpoint = CoreClientUtils.SelectEndpoint(appConfig, endpoint.Url, endpoint.SecurityModeEnum != MessageSecurityMode.None, 15000);
            ApplySecurity(endpoint, selectedEndpoint);
            var endpointConfig = EndpointConfiguration.Create(appConfig);
            var configuredEndpoint = new ConfiguredEndpoint(null, selectedEndpoint, endpointConfig);

            IUserIdentity userIdentity = string.IsNullOrWhiteSpace(endpoint.UserName)
                ? new UserIdentity(new AnonymousIdentityToken())
                : new UserIdentity(endpoint.UserName, endpoint.Password ?? string.Empty);

            session = await Session.Create(
                appConfig,
                configuredEndpoint,
                false,
                endpoint.Name,
                60000,
                userIdentity,
                null);

            report.ConnectMilliseconds = sw.Elapsed.TotalMilliseconds;
            Console.WriteLine($"[{endpoint.Name}] connected in {report.ConnectMilliseconds:n0} ms");

            var nodeIds = await ResolveNodesAsync(session, endpoint, runConfig, token);
            report.SymbolTableTotalPoints = nodeIds.Count;
            if (nodeIds.Count == 0)
            {
                report.Error = "No variable nodes resolved. Add nodeIds or browseRoots in config.";
                return report;
            }

            subscription = new Subscription(session.DefaultSubscription)
            {
                PublishingInterval = endpoint.PublishingIntervalMs ?? runConfig.DefaultPublishingIntervalMs,
                KeepAliveCount = 10,
                LifetimeCount = 30,
                MaxNotificationsPerPublish = 0,
                PublishingEnabled = true,
                Priority = 1
            };
            session.AddSubscription(subscription);
            subscription.Create();

            foreach (var nodeId in nodeIds)
            {
                var key = nodeId.ToString();
                var item = new MonitoredItem(subscription.DefaultItem)
                {
                    StartNodeId = nodeId,
                    AttributeId = Attributes.Value,
                    DisplayName = key,
                    SamplingInterval = endpoint.SamplingIntervalMs ?? runConfig.DefaultSamplingIntervalMs,
                    QueueSize = (uint)(endpoint.QueueSize ?? runConfig.DefaultQueueSize),
                    DiscardOldest = true
                };
                item.Notification += (monitoredItem, _) => OnNotification(endpoint.Name, key, monitoredItem, signalStats);
                subscription.AddItem(item);
            }

            subscription.ApplyChanges();
            report.MonitoredPoints = subscription.MonitoredItemCount;
            Console.WriteLine($"[{endpoint.Name}] monitoring {report.MonitoredPoints:n0} nodes");

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(runConfig.DurationSeconds), token);
            }
            catch (OperationCanceledException)
            {
                // Normal path when the shared run timer expires.
            }
        }
        catch (Exception ex)
        {
            report.Error = ex.Message;
            Console.Error.WriteLine($"[{endpoint.Name}] ERROR: {ex.Message}");
        }
        finally
        {
            try
            {
                subscription?.Delete(true);
                session?.Close();
                session?.Dispose();
            }
            catch
            {
                // Best effort cleanup after a field test run.
            }
        }

        report.Signals = signalStats.Values
            .Select(s => s.ToReport())
            .OrderByDescending(s => s.SampleCount)
            .ThenBy(s => s.NodeId, StringComparer.Ordinal)
            .ToList();
        report.ActualDataPoints = report.Signals.Count(s => s.SampleCount > 0);
        report.TotalSamples = report.Signals.Sum(s => s.SampleCount);
        report.ActivePointRatio = report.SymbolTableTotalPoints == 0 ? 0 : (double)report.ActualDataPoints / report.SymbolTableTotalPoints;
        report.AverageSamplesPerSecond = report.TotalSamples / Math.Max(1, sw.Elapsed.TotalSeconds);
        report.ElapsedSeconds = sw.Elapsed.TotalSeconds;
        return report;
    }

    private static void ApplySecurity(EndpointConfig endpoint, EndpointDescription selectedEndpoint)
    {
        if (!string.IsNullOrWhiteSpace(endpoint.SecurityPolicy))
        {
            selectedEndpoint.SecurityPolicyUri = endpoint.SecurityPolicyUri;
        }

        selectedEndpoint.SecurityMode = endpoint.SecurityModeEnum;
    }

    private static async Task<List<NodeId>> ResolveNodesAsync(Session session, EndpointConfig endpoint, LoadTestConfig runConfig, CancellationToken token)
    {
        var unique = new Dictionary<string, NodeId>(StringComparer.Ordinal);

        foreach (var value in endpoint.NodeIds)
        {
            var nodeId = ParseNodeId(value);
            unique[nodeId.ToString()] = nodeId;
        }

        foreach (var root in endpoint.BrowseRoots)
        {
            token.ThrowIfCancellationRequested();
            var rootNode = ParseNodeId(root);
            await BrowseVariablesAsync(session, rootNode, unique, runConfig.MaxBrowseNodesPerEndpoint, token);
        }

        return unique.Values.ToList();
    }

    private static NodeId ParseNodeId(string value)
    {
        if (string.Equals(value, "ObjectsFolder", StringComparison.OrdinalIgnoreCase))
        {
            return ObjectIds.ObjectsFolder;
        }

        return NodeId.Parse(value);
    }

    private static Task BrowseVariablesAsync(Session session, NodeId root, Dictionary<string, NodeId> result, int maxNodes, CancellationToken token)
    {
        var queue = new Queue<NodeId>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        queue.Enqueue(root);

        while (queue.Count > 0 && result.Count < maxNodes)
        {
            token.ThrowIfCancellationRequested();
            var node = queue.Dequeue();
            if (!visited.Add(node.ToString()))
            {
                continue;
            }

            session.Browse(
                null,
                null,
                node,
                0,
                BrowseDirection.Forward,
                ReferenceTypeIds.HierarchicalReferences,
                true,
                (uint)(NodeClass.Object | NodeClass.Variable),
                out var continuationPoint,
                out var references);

            AddReferences(session, references, queue, result, maxNodes);
            while (continuationPoint is { Length: > 0 } && result.Count < maxNodes)
            {
                session.BrowseNext(null, false, continuationPoint, out continuationPoint, out references);
                AddReferences(session, references, queue, result, maxNodes);
            }
        }

        return Task.CompletedTask;
    }

    private static void AddReferences(Session session, ReferenceDescriptionCollection references, Queue<NodeId> queue, Dictionary<string, NodeId> result, int maxNodes)
    {
        foreach (var reference in references)
        {
            if (reference.NodeId.IsAbsolute)
            {
                continue;
            }

            var nodeId = ExpandedNodeId.ToNodeId(reference.NodeId, session.NamespaceUris);
            if (nodeId is null)
            {
                continue;
            }

            if (reference.NodeClass == NodeClass.Variable)
            {
                result.TryAdd(nodeId.ToString(), nodeId);
                if (result.Count >= maxNodes)
                {
                    break;
                }
            }
            else if (reference.NodeClass == NodeClass.Object)
            {
                queue.Enqueue(nodeId);
            }
        }
    }

    private static void OnNotification(string endpointName, string nodeId, MonitoredItem monitoredItem, ConcurrentDictionary<string, SignalStats> signalStats)
    {
        foreach (var value in monitoredItem.DequeueValues())
        {
            var timestamp = value.SourceTimestamp != DateTime.MinValue
                ? value.SourceTimestamp
                : value.ServerTimestamp;

            if (timestamp == DateTime.MinValue)
            {
                timestamp = DateTime.UtcNow;
            }

            var stats = signalStats.GetOrAdd(nodeId, id => new SignalStats(endpointName, id));
            stats.Add(timestamp, StatusCode.IsGood(value.StatusCode));
        }
    }
}

static string? GetArgValue(string[] values, string name)
{
    for (var i = 0; i < values.Length - 1; i++)
    {
        if (string.Equals(values[i], name, StringComparison.OrdinalIgnoreCase))
        {
            return values[i + 1];
        }
    }

    var prefix = name + "=";
    var inline = values.FirstOrDefault(v => v.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    return inline is null ? null : inline[prefix.Length..];
}

static void PrintUsage()
{
    Console.WriteLine("""
    OPC UA Client Load Test

    Run with a config:
      opcua-client-loadtest.exe appsettings.json

    Generate an HD_OPC_UA sharded config:
      opcua-client-loadtest.exe --init-hd-opcua-config --host 192.168.3.166 --base-port 4840 --shards 16 --duration-seconds 1800 --out appsettings.hd-opcua.json

    Important config fields:
      durationSeconds              Collection duration.
      endpoints[].url              OPC UA endpoint URL.
      endpoints[].browseRoots      Variable browse roots, e.g. ns=2;s=HD Online Sampled Values.
      endpoints[].nodeIds          Explicit variable NodeIds, e.g. ns=2;s=Mac04\[45:496].
      publishingIntervalMs         Subscription publish interval.
      samplingIntervalMs           Monitored item sampling interval.
      queueSize                    Monitored item queue size.
    """);
}

static class HdOpcUaConfigFactory
{
    public static void Write(string path, string host, int basePort, int shards, int durationSeconds)
    {
        if (shards <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(shards), "Shard count must be greater than zero.");
        }

        var config = new LoadTestConfig
        {
            DurationSeconds = durationSeconds,
            OutputDirectory = "reports",
            AcceptUntrustedCertificates = true,
            DefaultPublishingIntervalMs = 500,
            DefaultSamplingIntervalMs = 500,
            DefaultQueueSize = 10,
            MaxBrowseNodesPerEndpoint = 50000,
            Endpoints = Enumerable.Range(0, shards)
                .Select(index => new EndpointConfig
                {
                    Name = $"HD_OPC_UA-Shard-{index:000}",
                    Url = $"opc.tcp://{host}:{basePort + index}",
                    SecurityPolicy = "None",
                    SecurityMode = "None",
                    PublishingIntervalMs = 500,
                    SamplingIntervalMs = 500,
                    QueueSize = 10,
                    BrowseRoots = new List<string> { "ns=2;s=HD Online Sampled Values" }
                })
                .ToList()
        };

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(config, ReportWriter.ConfigJsonOptions);
        File.WriteAllText(path, json, Encoding.UTF8);
    }
}

sealed class SignalStats
{
    private readonly object _gate = new();
    private readonly List<double> _intervalsMs = new();
    private DateTime? _lastTimestamp;
    private long _sampleCount;
    private long _goodCount;
    private long _badCount;

    public SignalStats(string endpointName, string nodeId)
    {
        EndpointName = endpointName;
        NodeId = nodeId;
    }

    public string EndpointName { get; }
    public string NodeId { get; }

    public void Add(DateTime timestamp, bool isGood)
    {
        lock (_gate)
        {
            if (_lastTimestamp.HasValue)
            {
                var delta = (timestamp - _lastTimestamp.Value).TotalMilliseconds;
                if (delta >= 0)
                {
                    _intervalsMs.Add(delta);
                }
            }

            FirstTimestamp ??= timestamp;
            LastTimestamp = timestamp;
            _lastTimestamp = timestamp;
            _sampleCount++;
            if (isGood)
            {
                _goodCount++;
            }
            else
            {
                _badCount++;
            }
        }
    }

    public DateTime? FirstTimestamp { get; private set; }
    public DateTime? LastTimestamp { get; private set; }

    public SignalReport ToReport()
    {
        lock (_gate)
        {
            var sorted = _intervalsMs.OrderBy(v => v).ToArray();
            return new SignalReport
            {
                EndpointName = EndpointName,
                NodeId = NodeId,
                SampleCount = _sampleCount,
                GoodSamples = _goodCount,
                BadSamples = _badCount,
                FirstTimestampUtc = FirstTimestamp,
                LastTimestampUtc = LastTimestamp,
                IntervalCount = sorted.Length,
                IntervalMinMs = sorted.Length == 0 ? null : sorted.First(),
                IntervalAverageMs = sorted.Length == 0 ? null : sorted.Average(),
                IntervalP50Ms = Percentile(sorted, 0.50),
                IntervalP95Ms = Percentile(sorted, 0.95),
                IntervalP99Ms = Percentile(sorted, 0.99),
                IntervalMaxMs = sorted.Length == 0 ? null : sorted.Last()
            };
        }
    }

    private static double? Percentile(double[] sorted, double percentile)
    {
        if (sorted.Length == 0)
        {
            return null;
        }

        var index = Math.Clamp((int)Math.Ceiling(percentile * sorted.Length) - 1, 0, sorted.Length - 1);
        return sorted[index];
    }
}

static class ReportWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static readonly JsonSerializerOptions ConfigJsonOptions = new(JsonOptions)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static void WriteJson(string path, RunReport report)
    {
        File.WriteAllText(path, JsonSerializer.Serialize(report, JsonOptions), Encoding.UTF8);
    }

    public static void WriteEndpointCsv(string path, RunReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("endpoint,url,error,symbol_table_total_points,actual_data_points,active_point_ratio,total_samples,avg_samples_per_second,connect_ms,elapsed_seconds");
        foreach (var endpoint in report.Endpoints)
        {
            sb.AppendCsv(endpoint.Name)
              .AppendCsv(endpoint.Url)
              .AppendCsv(endpoint.Error)
              .AppendCsv(endpoint.SymbolTableTotalPoints)
              .AppendCsv(endpoint.ActualDataPoints)
              .AppendCsv(endpoint.ActivePointRatio)
              .AppendCsv(endpoint.TotalSamples)
              .AppendCsv(endpoint.AverageSamplesPerSecond)
              .AppendCsv(endpoint.ConnectMilliseconds)
              .AppendCsv(endpoint.ElapsedSeconds, last: true)
              .AppendLine();
        }
        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
    }

    public static void WriteSignalCsv(string path, RunReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("endpoint,node_id,sample_count,good_samples,bad_samples,first_timestamp_utc,last_timestamp_utc,interval_count,interval_min_ms,interval_avg_ms,interval_p50_ms,interval_p95_ms,interval_p99_ms,interval_max_ms");
        foreach (var signal in report.Endpoints.SelectMany(e => e.Signals))
        {
            sb.AppendCsv(signal.EndpointName)
              .AppendCsv(signal.NodeId)
              .AppendCsv(signal.SampleCount)
              .AppendCsv(signal.GoodSamples)
              .AppendCsv(signal.BadSamples)
              .AppendCsv(signal.FirstTimestampUtc?.ToString("O"))
              .AppendCsv(signal.LastTimestampUtc?.ToString("O"))
              .AppendCsv(signal.IntervalCount)
              .AppendCsv(signal.IntervalMinMs)
              .AppendCsv(signal.IntervalAverageMs)
              .AppendCsv(signal.IntervalP50Ms)
              .AppendCsv(signal.IntervalP95Ms)
              .AppendCsv(signal.IntervalP99Ms)
              .AppendCsv(signal.IntervalMaxMs, last: true)
              .AppendLine();
        }
        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
    }

    public static void WriteHtml(string path, RunReport report)
    {
        var rows = string.Join(Environment.NewLine, report.Endpoints.Select(e => $"""
            <tr class="{(e.Error is null ? "" : "error")}">
              <td>{H(e.Name)}</td>
              <td>{H(e.Url)}</td>
              <td>{H(e.SymbolTableTotalPoints.ToString("n0"))}</td>
              <td>{H(e.ActualDataPoints.ToString("n0"))}</td>
              <td>{H(e.ActivePointRatio.ToString("P2"))}</td>
              <td>{H(e.TotalSamples.ToString("n0"))}</td>
              <td>{H(e.AverageSamplesPerSecond.ToString("n1"))}</td>
              <td>{H(e.ConnectMilliseconds.ToString("n0"))}</td>
              <td>{H(e.Error ?? "OK")}</td>
            </tr>
            """));

        var signalRows = string.Join(Environment.NewLine, report.Endpoints
            .SelectMany(e => e.Signals.Take(2000))
            .Select(s => $"""
            <tr>
              <td>{H(s.EndpointName)}</td>
              <td>{H(s.NodeId)}</td>
              <td>{H(s.SampleCount.ToString("n0"))}</td>
              <td>{H(s.GoodSamples.ToString("n0"))}</td>
              <td>{H(s.BadSamples.ToString("n0"))}</td>
              <td>{H(F(s.IntervalAverageMs))}</td>
              <td>{H(F(s.IntervalP95Ms))}</td>
              <td>{H(F(s.IntervalP99Ms))}</td>
              <td>{H(F(s.IntervalMaxMs))}</td>
            </tr>
            """));

        var html = $$"""
        <!doctype html>
        <html lang="zh-CN">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width, initial-scale=1">
          <title>OPC UA Client Load Test Report</title>
          <style>
            body { font-family: "Segoe UI", Arial, sans-serif; margin: 24px; color: #17202a; background: #f7f9fb; }
            h1, h2 { margin: 0 0 12px; }
            .meta { margin-bottom: 24px; color: #4d5b68; }
            table { border-collapse: collapse; width: 100%; margin: 12px 0 28px; background: #fff; }
            th, td { border: 1px solid #d8e0e8; padding: 8px 10px; text-align: left; font-size: 13px; vertical-align: top; }
            th { background: #e8eef5; position: sticky; top: 0; }
            tr.error { background: #fff0f0; }
            .note { color: #5f6f7c; font-size: 13px; }
          </style>
        </head>
        <body>
          <h1>OPC UA Client 接收效率测试报告</h1>
          <div class="meta">
            开始：{{H(report.StartedAt.ToString("yyyy-MM-dd HH:mm:ss zzz"))}} |
            结束：{{H(report.FinishedAt.ToString("yyyy-MM-dd HH:mm:ss zzz"))}} |
            主机：{{H(report.MachineName)}} |
            Endpoint 数：{{report.Endpoints.Count}}
          </div>

          <h2>Endpoint 汇总</h2>
          <table>
            <thead>
              <tr>
                <th>Endpoint</th><th>URL</th><th>符号表总点数</th><th>实际数据点数</th><th>活跃比例</th>
                <th>总样本数</th><th>样本/秒</th><th>连接耗时 ms</th><th>状态</th>
              </tr>
            </thead>
            <tbody>{{rows}}</tbody>
          </table>

          <h2>信号时间间隔明细</h2>
          <p class="note">HTML 仅展示每个 endpoint 样本数最高的前 2000 个信号；完整明细见 signals CSV/JSON。</p>
          <table>
            <thead>
              <tr>
                <th>Endpoint</th><th>NodeId</th><th>样本数</th><th>Good</th><th>Bad</th>
                <th>平均间隔 ms</th><th>P95 ms</th><th>P99 ms</th><th>最大间隔 ms</th>
              </tr>
            </thead>
            <tbody>{{signalRows}}</tbody>
          </table>
        </body>
        </html>
        """;

        File.WriteAllText(path, html, Encoding.UTF8);
    }

    private static string H(string value) => WebUtility.HtmlEncode(value);
    private static string F(double? value) => value?.ToString("n3") ?? "";
}

static class CsvExtensions
{
    public static StringBuilder AppendCsv(this StringBuilder sb, object? value, bool last = false)
    {
        var text = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "";
        if (text.Contains('"') || text.Contains(',') || text.Contains('\n') || text.Contains('\r'))
        {
            text = "\"" + text.Replace("\"", "\"\"") + "\"";
        }

        sb.Append(text);
        if (!last)
        {
            sb.Append(',');
        }
        return sb;
    }
}

sealed class LoadTestConfig
{
    public int DurationSeconds { get; set; } = 300;
    public string OutputDirectory { get; set; } = "reports";
    public bool AcceptUntrustedCertificates { get; set; } = true;
    public int DefaultPublishingIntervalMs { get; set; } = 1000;
    public int DefaultSamplingIntervalMs { get; set; } = 1000;
    public int DefaultQueueSize { get; set; } = 100;
    public int MaxBrowseNodesPerEndpoint { get; set; } = 50000;
    public List<EndpointConfig> Endpoints { get; set; } = new();
}

sealed class EndpointConfig
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public string? SecurityPolicy { get; set; }
    public string? SecurityMode { get; set; }
    public string? UserName { get; set; }
    public string? Password { get; set; }
    public int? PublishingIntervalMs { get; set; }
    public int? SamplingIntervalMs { get; set; }
    public int? QueueSize { get; set; }
    public List<string> BrowseRoots { get; set; } = new();
    public List<string> NodeIds { get; set; } = new();

    [JsonIgnore]
    public MessageSecurityMode SecurityModeEnum => SecurityMode?.Trim().ToLowerInvariant() switch
    {
        null or "" or "none" => MessageSecurityMode.None,
        "sign" => MessageSecurityMode.Sign,
        "signandencrypt" or "sign_and_encrypt" => MessageSecurityMode.SignAndEncrypt,
        _ => throw new InvalidOperationException($"Unsupported securityMode '{SecurityMode}'. Use None, Sign, or SignAndEncrypt.")
    };

    [JsonIgnore]
    public string SecurityPolicyUri => SecurityPolicy?.Trim().ToLowerInvariant() switch
    {
        null or "" or "none" => SecurityPolicies.None,
        "basic128rsa15" => SecurityPolicies.Basic128Rsa15,
        "basic256" => SecurityPolicies.Basic256,
        "basic256sha256" => SecurityPolicies.Basic256Sha256,
        "aes128_sha256_rsaoaep" or "aes128sha256rsaoaep" => SecurityPolicies.Aes128_Sha256_RsaOaep,
        "aes256_sha256_rsapss" or "aes256sha256rsapss" => SecurityPolicies.Aes256_Sha256_RsaPss,
        var uri when uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) => SecurityPolicy!,
        _ => throw new InvalidOperationException($"Unsupported securityPolicy '{SecurityPolicy}'.")
    };
}

sealed class RunReport
{
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset FinishedAt { get; set; }
    public double DurationSeconds { get; set; }
    public string MachineName { get; set; } = "";
    public List<EndpointReport> Endpoints { get; set; } = new();
}

sealed class EndpointReport
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public string? Error { get; set; }
    public int SymbolTableTotalPoints { get; set; }
    public int MonitoredPoints { get; set; }
    public int ActualDataPoints { get; set; }
    public double ActivePointRatio { get; set; }
    public long TotalSamples { get; set; }
    public double AverageSamplesPerSecond { get; set; }
    public double ConnectMilliseconds { get; set; }
    public double ElapsedSeconds { get; set; }
    public List<SignalReport> Signals { get; set; } = new();
}

sealed class SignalReport
{
    public string EndpointName { get; set; } = "";
    public string NodeId { get; set; } = "";
    public long SampleCount { get; set; }
    public long GoodSamples { get; set; }
    public long BadSamples { get; set; }
    public DateTime? FirstTimestampUtc { get; set; }
    public DateTime? LastTimestampUtc { get; set; }
    public int IntervalCount { get; set; }
    public double? IntervalMinMs { get; set; }
    public double? IntervalAverageMs { get; set; }
    public double? IntervalP50Ms { get; set; }
    public double? IntervalP95Ms { get; set; }
    public double? IntervalP99Ms { get; set; }
    public double? IntervalMaxMs { get; set; }
}
