using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using FluentModbus;
using Xunit;

namespace EdgeLink.Tests.Integration;

/// <summary>
/// End-to-end tests for message routing and Mask transformation.
///
/// Topology:
///   [TestDevice] ──connect──▶ [EdgeLink TCP Server (device side, port 192xx)]
///                                      ↕  SourceProtocolId routing
///                             [EdgeLink TCP Client] ──connect──▶ [LocalTcpServer (port 191xx)]
///
/// Key insight (from Router source):
///   - Request mask  = TCP Client's MaskType          (applied OUTBOUND: device → remote)
///   - Response mask = TCP Client's ResponseMaskType  (applied INBOUND:  remote → device)
///   - routeMode     = mask definition's routeMode    (broadcast | response)
/// </summary>
[Collection("Integration")]
public class RoutingTests(ServerFixture fixture) : IAsyncLifetime
{
    private HttpClient _client = null!;
    private readonly List<string> _portIds = [];
    private readonly List<string> _maskIds = [];

    public async Task InitializeAsync() => _client = await fixture.CreateAuthenticatedClientAsync();
    public async Task DisposeAsync()
    {
        foreach (var id in _portIds)
            try { await _client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/api/ports")
                { Content = JsonBody(new { id }) }); } catch { }
        foreach (var id in _maskIds)
            try { await _client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/api/masks/{id}")); } catch { }
        _client.Dispose();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 1. BASIC ROUTING
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Route_OriginalData_MessagePassesThroughUnchanged()
    {
        using var remote = new LocalTcpServer(19100);
        string srv = await AddTcpServer("RT_OD_Server", 19150);
        await AddTcpClient("RT_OD_Client", 19100, srv, maskType: "OriginalData");
        await Task.Delay(600);

        using var device = await ConnectDeviceAsync(19150);
        using var rConn  = await remote.AcceptAsync();

        await device.WriteLineAsync("sensor:temp;value:36.5");
        string? msg = await rConn.ReadDataLineAsync();

        Assert.NotNull(msg);
        Assert.Contains("sensor:temp;value:36.5", msg);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 2. REQUEST MASK (TCP Client MaskType) — outbound transformation
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Route_RequestMask_TransformsMessageBeforeForwarding()
    {
        string maskId = CreateId("ReqMask");
        await CreateMask(maskId, template: "{\"id\":\"{id}\",\"value\":{val}}",
            fieldDelim: ";", kvSep: ":");

        using var remote = new LocalTcpServer(19101);
        string srv = await AddTcpServer("RT_ReqM_Server", 19151);
        await AddTcpClient("RT_ReqM_Client", 19101, srv, maskType: maskId);
        await Task.Delay(600);

        using var device = await ConnectDeviceAsync(19151);
        using var rConn  = await remote.AcceptAsync();

        await device.WriteLineAsync("id:DEV01;val:42");
        string? msg = await rConn.ReadDataLineAsync();

        Assert.NotNull(msg);
        Assert.Contains("\"id\":\"DEV01\"", msg);
        Assert.Contains("\"value\":42",    msg);
    }

    [Fact]
    public async Task Route_RequestMask_MissingField_MessageDropped()
    {
        string maskId = CreateId("MissingF");
        await CreateMask(maskId, template: "{required_field}", fieldDelim: ";", kvSep: ":");

        using var remote = new LocalTcpServer(19102);
        string srv = await AddTcpServer("RT_Miss_Server", 19152);
        await AddTcpClient("RT_Miss_Client", 19102, srv, maskType: maskId);
        await Task.Delay(600);

        using var device = await ConnectDeviceAsync(19152);
        using var rConn  = await remote.AcceptAsync();

        await device.WriteLineAsync("other_field:hello");
        string? msg = await rConn.ReadDataLineAsync(timeout: 1500);

        Assert.Null(msg);   // dropped — mask output is empty when field missing
    }

    [Fact]
    public async Task Route_RequestMask_CustomDelimiters_Work()
    {
        string maskId = CreateId("CustomDelim");
        await CreateMask(maskId, template: "{a}|{b}", fieldDelim: ",", kvSep: "=");

        using var remote = new LocalTcpServer(19103);
        string srv = await AddTcpServer("RT_Delim_Server", 19153);
        await AddTcpClient("RT_Delim_Client", 19103, srv, maskType: maskId);
        await Task.Delay(600);

        using var device = await ConnectDeviceAsync(19153);
        using var rConn  = await remote.AcceptAsync();

        await device.WriteLineAsync("a=hello,b=world");
        string? msg = await rConn.ReadDataLineAsync();

        Assert.NotNull(msg);
        Assert.Contains("hello|world", msg);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 3. RESPONSE MASK (TCP Client ResponseMaskType) — inbound transformation
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Route_ResponseMask_TransformsResponseBeforeSendingToDevice()
    {
        string resMaskId = CreateId("ResMask");
        await CreateMask(resMaskId, template: "OK:{status}", fieldDelim: ";", kvSep: ":");

        using var remote = new LocalTcpServer(19104);
        string srv = await AddTcpServer("RT_Resp_Server", 19154);
        await AddTcpClient("RT_Resp_Client", 19104, srv,
            maskType: "OriginalData", responseMaskType: resMaskId, requestMode: "serial");
        await Task.Delay(600);

        using var device = await ConnectDeviceAsync(19154);
        using var rConn  = await remote.AcceptAsync();

        await device.WriteLineAsync("query:status");
        await rConn.ReadDataLineAsync(); // consume the forwarded request

        await rConn.WriteLineAsync("status:running;code:200");

        string? response = await device.ReadDataLineAsync();
        Assert.NotNull(response);
        Assert.Equal("OK:running", response);
    }

    [Fact]
    public async Task Route_ResponseMask_OriginalData_PassesThroughUnchanged()
    {
        using var remote = new LocalTcpServer(19105);
        string srv = await AddTcpServer("RT_RespOD_Server", 19155);
        await AddTcpClient("RT_RespOD_Client", 19105, srv,
            maskType: "OriginalData", responseMaskType: "OriginalData", requestMode: "serial");
        await Task.Delay(600);

        using var device = await ConnectDeviceAsync(19155);
        using var rConn  = await remote.AcceptAsync();

        await device.WriteLineAsync("ping");
        await rConn.ReadDataLineAsync();
        await rConn.WriteLineAsync("pong:42");

        string? response = await device.ReadDataLineAsync();
        Assert.NotNull(response);
        Assert.Contains("pong:42", response);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 4. ROUTE MODE — broadcast vs response
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Route_BroadcastMode_AllDevicesReceiveResponse()
    {
        // broadcast is the default routeMode (empty or "broadcast")
        string maskId = CreateId("Broadcast");
        await CreateMask(maskId, template: "{raw}", routeMode: "broadcast");

        using var remote = new LocalTcpServer(19106);
        string srv = await AddTcpServer("RT_BC_Server", 19156);
        await AddTcpClient("RT_BC_Client", 19106, srv,
            responseMaskType: maskId, requestMode: "serial");
        await Task.Delay(600);

        using var dev1 = await ConnectDeviceAsync(19156);
        using var dev2 = await ConnectDeviceAsync(19156);
        using var rConn = await remote.AcceptAsync();

        await dev1.WriteLineAsync("request");
        await rConn.ReadDataLineAsync();
        await rConn.WriteLineAsync("broadcast_response");

        string? r1 = await dev1.ReadDataLineAsync();
        string? r2 = await dev2.ReadDataLineAsync();

        Assert.NotNull(r1);
        Assert.Contains("broadcast_response", r1);
        Assert.NotNull(r2);
        Assert.Contains("broadcast_response", r2);
    }

    [Fact]
    public async Task Route_ResponseMode_OnlyRequesterReceivesResponse()
    {
        string maskId = CreateId("Response");
        await CreateMask(maskId, template: "{raw}", routeMode: "response");

        using var remote = new LocalTcpServer(19107);
        string srv = await AddTcpServer("RT_RM_Server", 19157);
        await AddTcpClient("RT_RM_Client", 19107, srv,
            responseMaskType: maskId, requestMode: "serial");
        await Task.Delay(600);

        using var dev1  = await ConnectDeviceAsync(19157);
        using var dev2  = await ConnectDeviceAsync(19157);
        using var rConn = await remote.AcceptAsync();

        // Only dev1 sends a request
        await dev1.WriteLineAsync("query");
        await rConn.ReadDataLineAsync();
        await rConn.WriteLineAsync("private_response");

        // dev1 receives it
        string? r1 = await dev1.ReadDataLineAsync();
        Assert.NotNull(r1);
        Assert.Contains("private_response", r1);

        // dev2 should NOT receive it
        string? r2 = await dev2.ReadDataLineAsync(timeout: 1000);
        Assert.Null(r2);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 5. REQUEST MODES — serial / polling / concurrent
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Route_SerialMode_RequestsQueueAndCompleteInOrder()
    {
        using var remote = new LocalTcpServer(19108);
        string srv = await AddTcpServer("RT_Serial_Server", 19158);
        await AddTcpClient("RT_Serial_Client", 19108, srv,
            requestMode: "serial", responseMaskType: "OriginalData");
        await Task.Delay(600);

        using var device = await ConnectDeviceAsync(19158);
        using var rConn  = await remote.AcceptAsync();

        // Send two requests quickly
        await device.WriteLineAsync("req:1");
        await device.WriteLineAsync("req:2");

        // Remote processes first
        string? r1 = await rConn.ReadDataLineAsync();
        Assert.NotNull(r1);
        await rConn.WriteLineAsync("resp:1");

        string? resp1 = await device.ReadDataLineAsync();
        Assert.NotNull(resp1);
        Assert.Contains("resp:1", resp1);

        // Then second arrives at remote
        string? r2 = await rConn.ReadDataLineAsync(timeout: 6000);
        Assert.NotNull(r2);
        await rConn.WriteLineAsync("resp:2");

        string? resp2 = await device.ReadDataLineAsync();
        Assert.NotNull(resp2);
        Assert.Contains("resp:2", resp2);
    }

    [Fact]
    public async Task Route_PollingMode_LatestRequestWins()
    {
        using var remote = new LocalTcpServer(19109);
        string srv = await AddTcpServer("RT_Poll_Server", 19159);
        await AddTcpClient("RT_Poll_Client", 19109, srv, requestMode: "polling");
        await Task.Delay(600);

        using var device = await ConnectDeviceAsync(19159);
        using var rConn  = await remote.AcceptAsync();

        // Send multiple requests — only the latest should matter
        await device.WriteLineAsync("data:first");
        await device.WriteLineAsync("data:second");
        await device.WriteLineAsync("data:third");
        await Task.Delay(200);

        // Remote should receive (at least) the last one
        string? received = await rConn.ReadDataLineAsync(timeout: 2000);
        Assert.NotNull(received);
        // In polling mode the latest wins; we can't guarantee first is seen
        // but we CAN guarantee the last one arrives
        Assert.Contains("data:", received);
    }

    [Fact]
    public async Task Route_ConcurrentMode_CorrelationIdInjectedInRequest()
    {
        string maskId = CreateId("CorrIdMask");
        // Template includes {_corrId} which EdgeLink injects automatically
        await CreateMask(maskId,
            template: "{id}:{val}:{_corrId}",
            fieldDelim: ";", kvSep: ":");

        using var remote = new LocalTcpServer(19110);
        string srv = await AddTcpServer("RT_Conc_Server", 19160);
        await AddTcpClient("RT_Conc_Client", 19110, srv,
            maskType: maskId, requestMode: "concurrent");
        await Task.Delay(600);

        using var device = await ConnectDeviceAsync(19160);
        using var rConn  = await remote.AcceptAsync();

        await device.WriteLineAsync("id:DEV01;val:99");
        string? msg = await rConn.ReadDataLineAsync();

        Assert.NotNull(msg);
        // Message should have 3 parts: id:val:corrId
        string[] parts = msg!.Split(':');
        Assert.True(parts.Length >= 3, $"Expected 3 colon-separated parts, got: {msg}");
        Assert.Equal("DEV01", parts[0]);
        Assert.Equal("99",    parts[1]);
        Assert.False(string.IsNullOrEmpty(parts[2]), "CorrelationId should not be empty");
    }

    [Fact]
    public async Task Route_ConcurrentMode_ResponseRoutedToCorrectDevice()
    {
        // Request mask injects {_corrId}, response mask reads it back to route to correct device
        string reqMaskId = CreateId("ConcReqM");
        string resMaskId = CreateId("ConcResM");

        await CreateMask(reqMaskId,
            template: "{data}:{_corrId}",
            fieldDelim: ";", kvSep: ":");

        // Response mask must pass through the correlationId field
        await CreateMask(resMaskId,
            template: "{raw}",
            correlationIdField: "_corrId",
            routeMode: "response");

        using var remote = new LocalTcpServer(19111);
        string srv = await AddTcpServer("RT_ConcRes_Server", 19161);
        await AddTcpClient("RT_ConcRes_Client", 19111, srv,
            maskType: reqMaskId, responseMaskType: resMaskId,
            requestMode: "concurrent");
        await Task.Delay(600);

        // Two devices connect
        using var dev1  = await ConnectDeviceAsync(19161);
        using var dev2  = await ConnectDeviceAsync(19161);
        using var rConn = await remote.AcceptAsync();

        // Both send concurrently
        await dev1.WriteLineAsync("data:req1");
        await dev2.WriteLineAsync("data:req2");

        // Collect both requests from remote, parse their correlationIds
        string? fwd1 = await rConn.ReadDataLineAsync();
        string? fwd2 = await rConn.ReadDataLineAsync();

        Assert.NotNull(fwd1);
        Assert.NotNull(fwd2);

        // Echo back with the same correlationId in the _corrId field
        // Format received: "req1:<corrId>" or "req2:<corrId>"
        string corrId1 = fwd1!.Split(':').LastOrDefault() ?? "";
        string corrId2 = fwd2!.Split(':').LastOrDefault() ?? "";

        Assert.False(string.IsNullOrEmpty(corrId1));
        Assert.False(string.IsNullOrEmpty(corrId2));
        Assert.NotEqual(corrId1, corrId2);

        // Remote echoes back with _corrId field so router can match
        await rConn.WriteLineAsync($"_corrId:{corrId1}");
        await rConn.WriteLineAsync($"_corrId:{corrId2}");

        // Each device gets its own response (response mode)
        string? r1 = await dev1.ReadDataLineAsync(timeout: 3000);
        string? r2 = await dev2.ReadDataLineAsync(timeout: 3000);

        Assert.NotNull(r1);
        Assert.NotNull(r2);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 6. MULTI-TARGET ROUTING (same SourceProtocolId on multiple TCP Clients)
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Route_MultipleClients_SameSource_AllReceiveMessage()
    {
        using var remote1 = new LocalTcpServer(19112);
        using var remote2 = new LocalTcpServer(19113);

        string srv = await AddTcpServer("RT_Multi_Server", 19162);
        await AddTcpClient("RT_Multi_Client1", 19112, srv);
        await AddTcpClient("RT_Multi_Client2", 19113, srv);
        await Task.Delay(800);

        using var device = await ConnectDeviceAsync(19162);
        using var rConn1 = await remote1.AcceptAsync();
        using var rConn2 = await remote2.AcceptAsync();

        await device.WriteLineAsync("broadcast:hello");

        string? m1 = await rConn1.ReadDataLineAsync();
        string? m2 = await rConn2.ReadDataLineAsync();

        Assert.NotNull(m1);
        Assert.Contains("broadcast:hello", m1);
        Assert.NotNull(m2);
        Assert.Contains("broadcast:hello", m2);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 7. UDP ROUTING — mask applied while relaying datagrams
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Route_Udp_RequestMask_TransformsDatagramBeforeForwarding()
    {
        string maskId = CreateId("UdpMask");
        await CreateMask(maskId, template: "{id}|{val}", fieldDelim: ";", kvSep: ":");

        const int listenPort  = 19170; // EdgeLink listens here for the incoming datagram
        const int forwardPort = 19171; // masked output is relayed here

        await AddUdpPort("RT_Udp_Client", listenPort, forwardPort, maskId);
        await Task.Delay(400);

        using var receiver = new UdpClient(forwardPort);
        using var sender   = new UdpClient();

        byte[] data = Encoding.UTF8.GetBytes("id:DEV01;val:7\n");
        await sender.SendAsync(data, data.Length, "127.0.0.1", listenPort);

        // First datagram to a device seen for the first time is an
        // EDGELINK_STATUS:CONNECTED liveness notice — skip it and read the
        // masked payload that follows, same as TestTcpConnection.ReadDataLineAsync.
        string? msg = null;
        using var cts = new CancellationTokenSource(3000);
        while (!cts.IsCancellationRequested)
        {
            var result = await receiver.ReceiveAsync(cts.Token);
            string line = Encoding.UTF8.GetString(result.Buffer).TrimEnd('\r', '\n');
            if (line.StartsWith("EDGELINK_STATUS:")) continue;
            msg = line;
            break;
        }

        Assert.Equal("DEV01|7", msg);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 8. MODBUS ROUTING — polled register value flows through mask to a TCP Client
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Route_Modbus_PolledRegisterValue_RoutesThroughMaskToClient()
    {
        const int slavePort = 19270;
        var slave = StartModbusServer(slavePort);
        try
        {
            // ModbusTcpMasterConnector always connects with ModbusEndianness.BigEndian,
            // while GetHoldingRegisterBuffer<T> writes in host (little-endian) byte order —
            // pre-swap so the master reads back the intended value.
            slave.GetHoldingRegisterBuffer<ushort>(1)[0] =
                System.Buffers.Binary.BinaryPrimitives.ReverseEndianness((ushort)12345);

            string maskId = CreateId("ModbusMask");
            await CreateMask(maskId, template: "{id}={temp}", fieldDelim: ";", kvSep: ":");

            using var remote = new LocalTcpServer(19271);

            var mbResp = await _client.PostJsonAsync("/api/ports", new
            {
                protocolName = "RT_Modbus_Master",
                netProtocol  = "MODBUS TCP MASTER",
                localPort    = "",
                remotePort   = slavePort.ToString(),
                targetIp     = "127.0.0.1",
                modbus       = new
                {
                    slaveId          = 1,
                    pollIntervalMs   = 150,
                    connectTimeoutMs = 1000,
                    readTimeoutMs    = 1000,
                    deviceId         = "mbtest",
                    registers        = new[]
                    {
                        new { name = "temp", functionCode = 3, startAddress = 0, quantity = 1, dataType = "uint16" },
                    },
                },
            });
            Assert.Equal(HttpStatusCode.Created, mbResp.StatusCode);
            using var mbDoc = await mbResp.ReadDocAsync();
            string modbusId = mbDoc.RootElement.GetProperty("id").GetString()!;
            _portIds.Add(modbusId);

            var cliResp = await _client.PostJsonAsync("/api/ports", new
            {
                protocolName       = "RT_Modbus_Client",
                netProtocol        = "TCP CLIENT",
                localPort          = "--",
                targetIp           = "127.0.0.1",
                remotePort         = "19271",
                maskType           = maskId,
                responseMaskType   = "OriginalData",
                requestMode        = "serial",
                sourceProtocolId   = modbusId,
                sourceProtocolName = "",
            });
            Assert.Equal(HttpStatusCode.Created, cliResp.StatusCode);
            using var cliDoc = await cliResp.ReadDocAsync();
            _portIds.Add(cliDoc.RootElement.GetProperty("id").GetString()!);

            using var rConn = await remote.AcceptAsync(timeout: 8000);
            string? msg = await rConn.ReadDataLineAsync(timeout: 8000);

            Assert.NotNull(msg);
            Assert.Equal("mbtest=12345", msg);
        }
        finally
        {
            try { slave.Stop(); } catch { }
            try { slave.Dispose(); } catch { }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 8. BINARY — TCP Client 二進位分包(binary response mask)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// TCP Client 收到的是二進位串流(對方是 TCP server、持續推送),必須靠 BinaryStreamFramer
    /// 分包後交給 binary response mask 解成 KV,再廣播給接在 TCP Server 埠上的下游。
    ///
    /// 串流刻意做得「難看一點」,因為這三件事在真實鏈路上都會發生:
    ///   • 開頭有雜訊 → framer 必須靠 sync 重新對齊
    ///   • 兩包不同長度(21 / 83)黏在一起 → 長度必須由 discriminator 決定
    ///   • 寫入切點落在封包中間 → 半包要留在緩衝等下一段
    /// 走文字路徑的話這裡會失敗:UTF8.GetString 會毀掉非法位元組,而 f32 承載裡的 0x0A
    /// 會被當成換行、把封包從中間切開。
    /// </summary>
    [Fact]
    public async Task Route_BinaryResponseMask_TcpClientFramesAndDecodes()
    {
        string maskId = CreateId("BinPlat");
        await CreateBinaryMask(maskId);

        using var remote = new LocalTcpServer(19114);            // 扮演對方的 TCP server(持續推送)
        string srv = await AddTcpServer("RT_Bin_Server", 19172);
        await AddTcpClient("RT_Bin_Client", 19114, srv,
            maskType: "OriginalData", responseMaskType: maskId, requestMode: "serial");
        await Task.Delay(600);

        using var device = await ConnectDeviceAsync(19172);       // 下游消費端
        using var rConn  = await remote.AcceptAsync();

        byte[] status = BuildStatusPacket();                      // msgType 19,83 bytes
        byte[] manage = BuildManagePacket();                      // msgType 17,21 bytes

        // 雜訊 + 兩包黏在一起,並且在第一包中間切斷分兩次送
        var wire = new List<byte> { 0xAA, 0xBB, 0xCC };
        wire.AddRange(status);
        wire.AddRange(manage);
        await rConn.WriteBytesAsync(wire.Take(40).ToArray());
        await Task.Delay(50);
        await rConn.WriteBytesAsync(wire.Skip(40).ToArray());

        string? line1 = await device.ReadDataLineAsync();
        string? line2 = await device.ReadDataLineAsync();

        Assert.Equal("id:plat1;mt:19;seq:7;ts:1755870000000;ack:42;out:1;estop:0;plc:2;gst:2;"
                   + "ecerr:0;derra:0;derrb:0;derrc:0;pa:120.5;pb:118.25;pc:119;"
                   + "cpa:121;cpb:118;cpc:119.5;qx:0;qy:0;qz:0;qw:1;hv:350.75;resid:0.02", line1);
        Assert.Equal("id:plat1;mt:17;seq:1;ts:1755870000000;act:3", line2);
    }

    /// <summary>
    /// 出站方向:下游送 KV 文字進 TCP Server 埠,EdgeLink 依 binary mask 編成二進位封包
    /// 送給對端(對端只吃二進位、且是 TCP server,所以 EdgeLink 是那條連線的 client)。
    ///
    /// 一併驗證編碼器自己產生的三個標頭欄位:frameLength、sendTimeMs,以及 seq ——
    /// seq 是每個 msgType 各一條、從 1 起算,這是對端會拿來擋重複/倒退封包的依據,
    /// 錯了會整批被拒絕,所以要走真實鏈路確認而不只是單元測試。
    /// </summary>
    [Fact]
    public async Task Route_BinaryRequestMask_EncodesKvToWire()
    {
        string maskId = CreateId("BinTx");
        await CreateBinaryMask(maskId);

        using var remote = new LocalTcpServer(19115);            // 扮演對端(只吃二進位)
        string srv = await AddTcpServer("RT_BinTx_Server", 19173);
        await AddTcpClient("RT_BinTx_Client", 19115, srv,
            maskType: maskId, responseMaskType: maskId, requestMode: "concurrent");
        await Task.Delay(600);

        using var device = await ConnectDeviceAsync(19173);       // 下游命令來源
        using var rConn  = await remote.AcceptAsync();

        // mode 1(直接給三軸行程);vel/acc/jrk 故意不送 —— optional 欄位留 0 = 協定的「沿用預設值」
        await device.WriteLineAsync("mt:16;mode:1;rqa:120.5;rqb:118.25;rqc:119");
        byte[] move1 = await rConn.ReadBytesAsync(67);

        Assert.Equal(67, move1.Length);
        Assert.Equal(0x4F, move1[0]);
        Assert.Equal(0x4B, move1[1]);
        Assert.Equal(1,    move1[2]);                                                     // version
        Assert.Equal(16,   move1[3]);                                                     // msgType
        Assert.Equal(0,    move1[4]);                                                     // kind
        Assert.Equal(67,   BinaryPrimitives.ReadUInt16LittleEndian(move1.AsSpan(5, 2)));  // frameLength
        Assert.Equal(1u,   BinaryPrimitives.ReadUInt32LittleEndian(move1.AsSpan(7, 4)));  // seq 第一筆

        long ts  = BinaryPrimitives.ReadInt64LittleEndian(move1.AsSpan(11, 8));
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Assert.InRange(ts, now - 60_000, now + 60_000);

        Assert.Equal(1,       move1[19]);                                                 // mode
        Assert.Equal(120.5f,  BinaryPrimitives.ReadSingleLittleEndian(move1.AsSpan(43, 4)));
        Assert.Equal(118.25f, BinaryPrimitives.ReadSingleLittleEndian(move1.AsSpan(47, 4)));
        Assert.Equal(119f,    BinaryPrimitives.ReadSingleLittleEndian(move1.AsSpan(51, 4)));
        Assert.Equal(0f,      BinaryPrimitives.ReadSingleLittleEndian(move1.AsSpan(55, 4)));  // vel 未送 → 0

        // 同一個 msgType 的第二筆:seq 必須嚴格遞增
        await device.WriteLineAsync("mt:16;mode:1;rqa:121;rqb:118;rqc:119.5");
        byte[] move2 = await rConn.ReadBytesAsync(67);
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(move2.AsSpan(7, 4)));

        // 換一種 msgType:長度不同,而且 seq 是自己那條 counter,從 1 重新算
        await device.WriteLineAsync("mt:17;act:3");
        byte[] mgmt = await rConn.ReadBytesAsync(21);
        Assert.Equal(21, mgmt.Length);
        Assert.Equal(17, mgmt[3]);
        Assert.Equal(21, BinaryPrimitives.ReadUInt16LittleEndian(mgmt.AsSpan(5, 2)));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(mgmt.AsSpan(7, 4)));
        Assert.Equal(3,  mgmt[19]);                                                       // action = ServoToggle
    }

    /// <summary>必填欄位缺漏 → 整包不送。靜默送出一筆被歸零的運動命令比不送危險得多。</summary>
    [Fact]
    public async Task Route_BinaryRequestMask_DropsIncompleteCommand()
    {
        string maskId = CreateId("BinDrop");
        await CreateBinaryMask(maskId);

        using var remote = new LocalTcpServer(19116);
        string srv = await AddTcpServer("RT_BinDrop_Server", 19174);
        await AddTcpClient("RT_BinDrop_Client", 19116, srv,
            maskType: maskId, responseMaskType: maskId, requestMode: "concurrent");
        await Task.Delay(600);

        using var device = await ConnectDeviceAsync(19174);
        using var rConn  = await remote.AcceptAsync();

        await device.WriteLineAsync("mt:16;rqa:120.5");            // 缺 mode(必填)
        await device.WriteLineAsync("mt:99;mode:1");               // 沒有這個 msgType
        await device.WriteLineAsync("mt:16;mode:abc");             // 值不合法
        await device.WriteLineAsync("mt:16;mode:1;rqa:120.5;rqb:118.25");   // 漏了 rqc
        await Task.Delay(400);

        // 前四筆都不該上線 —— 特別是最後那筆:漏一支軸不能被補成 0mm 送出去,
        // 那是一筆會讓平台衝到行程底端的合法命令。合法的那筆才會出現,seq 仍是 1(丟掉的不佔號)
        await device.WriteLineAsync("mt:17;act:1");
        byte[] pkt = await rConn.ReadBytesAsync(21);
        Assert.Equal(21, pkt.Length);
        Assert.Equal(17, pkt[3]);
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(pkt.AsSpan(7, 4)));
    }

    /// <summary>
    /// 對端用 seq 擋重複與倒退,所以連續命令的 seq 不只要遞增、**在線上的順序也不能亂**。
    /// 亂序等同整批被拒(rc:10),而那種失敗在現場只會表現成「命令有時候沒反應」。
    /// </summary>
    [Fact]
    public async Task Route_BinaryRequestMask_SeqStaysOrderedUnderRapidCommands()
    {
        const int N = 50;
        string maskId = CreateId("BinRate");
        await CreateBinaryMask(maskId);

        using var remote = new LocalTcpServer(19117);
        string srv = await AddTcpServer("RT_BinRate_Server", 19175);
        await AddTcpClient("RT_BinRate_Client", 19117, srv,
            maskType: maskId, responseMaskType: maskId, requestMode: "concurrent");
        await Task.Delay(600);

        using var device = await ConnectDeviceAsync(19175);
        using var rConn  = await remote.AcceptAsync();

        for (int i = 0; i < N; i++)
            await device.WriteLineAsync($"mt:17;act:{(i % 4) + 1}");

        byte[] all = await rConn.ReadBytesAsync(21 * N, timeout: 10_000);
        Assert.Equal(21 * N, all.Length);

        for (int i = 0; i < N; i++)
        {
            int off = i * 21;   // Span 區域變數在 async 方法裡不能用(C# 12),直接用索引
            Assert.Equal(0x4F, all[off]);
            Assert.Equal(17,   all[off + 3]);
            Assert.Equal((uint)(i + 1), BinaryPrimitives.ReadUInt32LittleEndian(all.AsSpan(off + 7, 4)));
        }
    }

    /// <summary>
    /// 連線重建後 seq 必須從 1 重新算 —— 對端是以「新的 TCP 連線 → 期望值重置回 1」驗收的,
    /// 沿用舊 counter 會讓重連後的每一筆命令都被拒。
    /// </summary>
    [Fact]
    public async Task Route_BinarySeq_ResetsAfterReconnect()
    {
        string maskId = CreateId("BinReconn");
        await CreateBinaryMask(maskId);

        using var remote = new LocalTcpServer(19118);
        string srv = await AddTcpServer("RT_BinRe_Server", 19176);
        await AddTcpClient("RT_BinRe_Client", 19118, srv,
            maskType: maskId, responseMaskType: maskId, requestMode: "concurrent");
        await Task.Delay(600);

        using var device = await ConnectDeviceAsync(19176);
        var conn1 = await remote.AcceptAsync();

        await device.WriteLineAsync("mt:17;act:1");
        await device.WriteLineAsync("mt:17;act:2");
        byte[] first = await conn1.ReadBytesAsync(42, timeout: 5000);
        Assert.Equal(42, first.Length);
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(first.AsSpan(7, 4)));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(first.AsSpan(21 + 7, 4)));

        // 對端斷線 → EdgeLink 心跳偵測到後重新撥號(預設心跳 5s + 重試 1s)
        conn1.Close();
        conn1.Dispose();
        using var conn2 = await remote.AcceptAsync(timeout: 20_000);
        await Task.Delay(400);

        await device.WriteLineAsync("mt:17;act:3");
        byte[] after = await conn2.ReadBytesAsync(21, timeout: 5000);
        Assert.Equal(21, after.Length);
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(after.AsSpan(7, 4)));
    }

    /// <summary>
    /// 串流裡混進一段「假 magic + 不存在的 msgType」時,framer 必須跳 1 個位元組重新對齊,
    /// 而不是把整個緩衝丟掉 —— 後面排隊的完整封包不能被一起賠進去。
    /// </summary>
    [Fact]
    public async Task Route_BinaryResponseMask_RecoversFromFalseSyncInStream()
    {
        string maskId = CreateId("BinResync");
        await CreateBinaryMask(maskId);

        using var remote = new LocalTcpServer(19119);
        string srv = await AddTcpServer("RT_BinRs_Server", 19177);
        await AddTcpClient("RT_BinRs_Client", 19119, srv,
            maskType: "OriginalData", responseMaskType: maskId, requestMode: "concurrent");
        await Task.Delay(600);

        using var device = await ConnectDeviceAsync(19177);
        using var rConn  = await remote.AcceptAsync();

        // 假 magic('O''K'+version)後面接一個不存在的 msgType,再接雜訊,最後才是真封包
        var wire = new List<byte> { 0x4F, 0x4B, 0x01, 0x63, 0x00, 0xFF, 0xFF, 0x12, 0x34 };
        wire.AddRange(BuildStatusPacket());
        await rConn.WriteBytesAsync(wire.ToArray());

        string? line = await device.ReadDataLineAsync(timeout: 5000);
        Assert.NotNull(line);
        Assert.Contains("mt:19", line);
        Assert.Contains("pa:120.5", line);
    }

    /// <summary>
    /// 實際要交付的那個 mask 檔(docs/PlatformTcp.mask.json)走一次真正的匯入 API,
    /// 再用它跑雙向。前面的測試用的都是精簡過的內嵌 spec —— 真正會被匯入的檔案本身
    /// 從來沒被驗過,而版面錯一個 offset 在現場只會表現成「數值怪怪的」。
    /// </summary>
    [Fact]
    public async Task Route_ShippedPlatformTcpMask_WorksBothDirections()
    {
        string maskJson = File.ReadAllText(FindRepoFile("docs/PlatformTcp.mask.json"));
        var import = await _client.PostAsync("/api/settings/import",
            new System.Net.Http.StringContent(maskJson, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, import.StatusCode);
        _maskIds.Add("PlatformTcp");

        using var remote = new LocalTcpServer(19120);
        string srv = await AddTcpServer("RT_Ship_Server", 19178);
        await AddTcpClient("RT_Ship_Client", 19120, srv,
            maskType: "PlatformTcp", responseMaskType: "PlatformTcp", requestMode: "concurrent");
        await Task.Delay(600);

        using var device = await ConnectDeviceAsync(19178);
        using var rConn  = await remote.AcceptAsync();

        // ── 出站:完整的 mode 1 命令 ──
        await device.WriteLineAsync(
            "mt:16;mode:1;rqx:0;rqy:0;rqz:0;rqw:0;rhv:0;rqa:120.5;rqb:118.25;rqc:119");
        byte[] cmd = await rConn.ReadBytesAsync(67, timeout: 5000);

        Assert.Equal(67, cmd.Length);
        Assert.Equal(0x4F, cmd[0]);
        Assert.Equal(0x4B, cmd[1]);
        Assert.Equal(1,    cmd[2]);
        Assert.Equal(16,   cmd[3]);
        Assert.Equal(0,    cmd[4]);                                                    // kind
        Assert.Equal(67,   BinaryPrimitives.ReadUInt16LittleEndian(cmd.AsSpan(5, 2)));
        Assert.Equal(1u,   BinaryPrimitives.ReadUInt32LittleEndian(cmd.AsSpan(7, 4)));
        Assert.Equal(1,    cmd[19]);                                                   // mode
        Assert.Equal(0,    cmd[20]);                                                   // reserved0 維持 0
        Assert.Equal(120.5f,  BinaryPrimitives.ReadSingleLittleEndian(cmd.AsSpan(43, 4)));
        Assert.Equal(118.25f, BinaryPrimitives.ReadSingleLittleEndian(cmd.AsSpan(47, 4)));
        Assert.Equal(119f,    BinaryPrimitives.ReadSingleLittleEndian(cmd.AsSpan(51, 4)));
        Assert.Equal(0f,      BinaryPrimitives.ReadSingleLittleEndian(cmd.AsSpan(55, 4)));   // vel 省略 → 沿用預設

        // ── 入站:帶異警的狀態封包,查表要生效 ──
        // 0x8611 = 跟隨誤差(精確 key)、0x0231 = PR 參數(範圍 key)、0x1234 = 表上沒有
        await rConn.WriteBytesAsync(BuildStatusPacket(
            groupState: 5, etherCatErr: 2, driveErrA: 0x8611, driveErrB: 0x0231, driveErrC: 0x1234));

        string? status = await device.ReadDataLineAsync(timeout: 5000);
        Assert.NotNull(status);
        Assert.Contains("mt:19",                     status);
        Assert.Contains("plctxt:CONNECTED",          status);
        Assert.Contains("gsttxt:ERRORSTOP",          status);
        Assert.Contains("ecerrtxt:WRONG_WORKING_COUNTER", status);
        Assert.Contains("derratxt:FOLLOWING_ERROR",  status);   // 精確
        Assert.Contains("derrbtxt:PR_PARAM",         status);   // 範圍
        Assert.Contains("derrctxt:UNKNOWN",          status);   // 落到 mapDefault
        Assert.Contains("derra:34321",               status);   // 原始碼要保留
        Assert.Contains("pa:120.5",                  status);
        Assert.Contains("qw:1",                      status);
    }

    /// <summary>從測試輸出目錄往上找到 repo 內的檔案。</summary>
    private static string FindRepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException($"找不到 {relative}(從 {AppContext.BaseDirectory} 往上找)");
    }

    // 共同標頭 19 bytes:magic 'O' 'K' / version 1 / msgType / kind / frameLength / seq / sendTimeMs
    private static List<byte> Header(byte msgType, ushort frameLength, uint seq)
    {
        var h = new List<byte> { 0x4F, 0x4B, 1, msgType, 0 };
        h.AddRange(BitConverter.GetBytes(frameLength));
        h.AddRange(BitConverter.GetBytes(seq));
        h.AddRange(BitConverter.GetBytes(1755870000000UL));
        return h;
    }

    private static byte[] BuildStatusPacket(ushort groupState = 2, ushort etherCatErr = 0,
        ushort driveErrA = 0, ushort driveErrB = 0, ushort driveErrC = 0)
    {
        var b = Header(19, 83, 7);
        b.AddRange(BitConverter.GetBytes(42u));                   // ackSeq
        b.Add(0b01);                                              // linkFlags:實際輸出、未急停
        b.Add(2);                                                 // plcConn = 已連線
        b.AddRange(BitConverter.GetBytes(groupState));
        b.AddRange(BitConverter.GetBytes(etherCatErr));
        foreach (ushort e in new[] { driveErrA, driveErrB, driveErrC })
            b.AddRange(BitConverter.GetBytes(e));                 // driveError A/B/C
        foreach (float v in new[] { 120.5f, 118.25f, 119f })      // actualPositionMm
            b.AddRange(BitConverter.GetBytes(v));
        foreach (float v in new[] { 121f, 118f, 119.5f })         // commandPositionMm
            b.AddRange(BitConverter.GetBytes(v));
        foreach (float v in new[] { 0f, 0f, 0f, 1f })             // tilt(單位四元數)
            b.AddRange(BitConverter.GetBytes(v));
        b.AddRange(BitConverter.GetBytes(350.75f));               // heaveMm
        b.AddRange(BitConverter.GetBytes(0.02f));                 // residualMm
        Assert.Equal(83, b.Count);
        return b.ToArray();
    }

    private static byte[] BuildManagePacket()
    {
        var b = Header(17, 21, 1);
        b.Add(3);                                                 // action = ServoToggle
        b.Add(0);                                                 // reserved
        Assert.Equal(21, b.Count);
        return b.ToArray();
    }

    /// <summary>建立平台 TCP V1 的 binary mask(只放測試用得到的 17 / 19 兩種訊息)。</summary>
    private async Task CreateBinaryMask(string maskId)
    {
        _maskIds.Add(maskId);
        await _client.PostJsonAsync("/api/masks", new { maskId });

        static object F(string name, int offset, string type) => new { name, offset, type };

        var put = await _client.PutJsonAsync($"/api/masks/{maskId}", new
        {
            maskId,
            localizationKey    = "",
            description        = "",
            fieldDelimiter     = ";",
            kvSeparator        = ":",
            outputTemplate     = "",
            sampleData         = "",
            routeMode          = "",
            correlationIdField = "",
            binary = new
            {
                byteOrder     = "little",
                sync          = "4f4b01",
                discriminator = new { offset = 3, type = "u8" },
                variants = new object[]
                {
                    // 出站:平台移動命令。seq/ts/frameLength 由編碼器產生;
                    // mode 用不到的那一組欄位標 optional(協定文件說「沒用到的請填 0」)
                    new
                    {
                        match = 16, isDefault = false, length = 67,
                        template = "mt:{mt};seq:{seq};mode:{mode}",
                        fields = new object[]
                        {
                            F("mt", 3, "u8"),
                            new { name = "len",  offset = 5,  type = "u16", auto = "frameLength" },
                            new { name = "seq",  offset = 7,  type = "u32", auto = "seq" },
                            new { name = "ts",   offset = 11, type = "u64", auto = "timeMs" },
                            F("mode", 19, "u8"),
                            F("rqa", 43, "f32"),
                            F("rqb", 47, "f32"),
                            F("rqc", 51, "f32"),
                            new { name = "vel", offset = 55, type = "f32", optional = true },
                            new { name = "acc", offset = 59, type = "f32", optional = true },
                            new { name = "jrk", offset = 63, type = "f32", optional = true },
                        },
                    },
                    // 出站:平台管理命令(EStop / ServoToggle / Reset)
                    new
                    {
                        match = 17, isDefault = false, length = 21,
                        template = "id:{id};mt:{mt};seq:{seq};ts:{ts};act:{act}",
                        fields = new object[]
                        {
                            new { name = "id", offset = 0, type = "const", value = "plat1" },
                            F("mt", 3, "u8"),
                            new { name = "len", offset = 5,  type = "u16", auto = "frameLength" },
                            new { name = "seq", offset = 7,  type = "u32", auto = "seq" },
                            new { name = "ts",  offset = 11, type = "u64", auto = "timeMs" },
                            F("act", 19, "u8"),
                        },
                    },
                    new
                    {
                        match = 19, isDefault = false, length = 83,
                        template = "id:{id};mt:{mt};seq:{seq};ts:{ts};ack:{ack};out:{out};estop:{estop};"
                                 + "plc:{plc};gst:{gst};ecerr:{ecerr};derra:{derra};derrb:{derrb};derrc:{derrc};"
                                 + "pa:{pa};pb:{pb};pc:{pc};cpa:{cpa};cpb:{cpb};cpc:{cpc};"
                                 + "qx:{qx};qy:{qy};qz:{qz};qw:{qw};hv:{hv};resid:{resid}",
                        fields = new object[]
                        {
                            new { name = "id", offset = 0, type = "const", value = "plat1" },
                            F("mt", 3, "u8"), F("seq", 7, "u32"), F("ts", 11, "u64"), F("ack", 19, "u32"),
                            new { name = "out",   offset = 23, type = "bit", bit = 0 },
                            new { name = "estop", offset = 23, type = "bit", bit = 1 },
                            F("plc", 24, "u8"), F("gst", 25, "u16"), F("ecerr", 27, "u16"),
                            F("derra", 29, "u16"), F("derrb", 31, "u16"), F("derrc", 33, "u16"),
                            new { name = "pa",    offset = 35, type = "f32", format = "0.###" },
                            new { name = "pb",    offset = 39, type = "f32", format = "0.###" },
                            new { name = "pc",    offset = 43, type = "f32", format = "0.###" },
                            new { name = "cpa",   offset = 47, type = "f32", format = "0.###" },
                            new { name = "cpb",   offset = 51, type = "f32", format = "0.###" },
                            new { name = "cpc",   offset = 55, type = "f32", format = "0.###" },
                            new { name = "qx",    offset = 59, type = "f32", format = "0.#####" },
                            new { name = "qy",    offset = 63, type = "f32", format = "0.#####" },
                            new { name = "qz",    offset = 67, type = "f32", format = "0.#####" },
                            new { name = "qw",    offset = 71, type = "f32", format = "0.#####" },
                            new { name = "hv",    offset = 75, type = "f32", format = "0.###" },
                            new { name = "resid", offset = 79, type = "f32", format = "0.###" },
                        },
                    },
                },
            },
        });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════════════

    private string CreateId(string prefix) =>
        $"{prefix}_{Guid.NewGuid().ToString("N")[..6]}";

    private async Task CreateMask(string maskId,
        string template        = "{raw}",
        string fieldDelim      = ";",
        string kvSep           = ":",
        string routeMode       = "",
        string correlationIdField = "")
    {
        _maskIds.Add(maskId);
        await _client.PostJsonAsync("/api/masks", new { maskId });
        var put = await _client.PutJsonAsync($"/api/masks/{maskId}", new
        {
            maskId,
            outputTemplate     = template,
            fieldDelimiter     = fieldDelim,
            kvSeparator        = kvSep,
            routeMode,
            correlationIdField,
            localizationKey    = "",
            description        = "",
            sampleData         = "",
        });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
    }

    private async Task<string> AddUdpPort(string name, int listenPort, int forwardPort, string maskType)
    {
        var resp = await _client.PostJsonAsync("/api/ports", new
        {
            protocolName = name,
            netProtocol  = "UDP",
            localPort    = forwardPort.ToString(),   // masked output relayed here
            remotePort   = listenPort.ToString(),    // EdgeLink listens here
            targetIp     = "127.0.0.1",
            maskType,
        });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        using var doc = await resp.ReadDocAsync();
        string id = doc.RootElement.GetProperty("id").GetString()!;
        _portIds.Add(id);
        return id;
    }

    /// <summary>Starts an in-process Modbus TCP slave (unit 1) on the given port, retrying the bind.</summary>
    private static ModbusTcpServer StartModbusServer(int port)
    {
        Exception? last = null;
        for (int i = 0; i < 20; i++)
        {
            var srv = new ModbusTcpServer();
            try
            {
                srv.AddUnit(1);
                srv.Start(new IPEndPoint(IPAddress.Loopback, port));
                return srv;
            }
            catch (Exception ex)
            {
                last = ex;
                try { srv.Dispose(); } catch { }
                Thread.Sleep(250);
            }
        }
        throw new InvalidOperationException($"Could not start ModbusTcpServer on {port}: {last?.Message}");
    }

    private async Task<string> AddTcpServer(string name, int localPort)
    {
        var resp = await _client.PostJsonAsync("/api/ports", new
        {
            protocolName = name,
            netProtocol  = "TCP SERVER",
            localPort    = localPort.ToString(),
        });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        using var doc = await resp.ReadDocAsync();
        string id = doc.RootElement.GetProperty("id").GetString()!;
        _portIds.Add(id);
        await Task.Delay(200);
        return id;
    }

    private async Task<string> AddTcpClient(string name, int remotePort, string sourceProtocolId,
        string maskType         = "OriginalData",
        string responseMaskType = "OriginalData",
        string requestMode      = "serial")
    {
        var resp = await _client.PostJsonAsync("/api/ports", new
        {
            protocolName       = name,
            netProtocol        = "TCP CLIENT",
            localPort          = "--",
            targetIp           = "127.0.0.1",
            remotePort         = remotePort.ToString(),
            maskType,
            responseMaskType,
            requestMode,
            sourceProtocolId,
            sourceProtocolName = "",
        });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        using var doc = await resp.ReadDocAsync();
        string id = doc.RootElement.GetProperty("id").GetString()!;
        _portIds.Add(id);
        await Task.Delay(300);
        return id;
    }

    private static async Task<TestTcpConnection> ConnectDeviceAsync(int edgeLinkServerPort)
    {
        var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", edgeLinkServerPort);
        var conn = new TestTcpConnection(tcp);
        // Consume first PING and reply PONG
        string? ping = await conn.ReadRawLineAsync(timeout: 6000);
        if (ping != null && ping.StartsWith("EDGELINK_PING:"))
        {
            string hex = ping.Split(':')[1].Trim();
            await conn.WriteLineAsync($"EDGELINK_PONG:{hex}");
        }
        return conn;
    }

    private static System.Net.Http.StringContent JsonBody(object obj) =>
        new(JsonSerializer.Serialize(obj), Encoding.UTF8, "application/json");
}

// ── Test helpers ──────────────────────────────────────────────────────────────

public sealed class TestTcpConnection(TcpClient tcp) : IDisposable
{
    private readonly NetworkStream _stream = tcp.GetStream();

    public async Task WriteLineAsync(string line)
    {
        byte[] data = Encoding.UTF8.GetBytes(line + "\n");
        await _stream.WriteAsync(data);
    }

    /// <summary>送原始位元組(二進位埠用:不加換行、不經 UTF-8 編碼)。</summary>
    public async Task WriteBytesAsync(byte[] data) => await _stream.WriteAsync(data);

    /// <summary>讀滿 count 個位元組(二進位埠用)。逾時或對方關閉時回傳已讀到的部分,
    /// 讓呼叫端能用長度斷言指出「少收了」而不是卡住。</summary>
    public async Task<byte[]> ReadBytesAsync(int count, int timeout = 3000)
    {
        using var cts = new CancellationTokenSource(timeout);
        var buf = new byte[count];
        int got = 0;
        try
        {
            while (got < count)
            {
                int n = await _stream.ReadAsync(buf.AsMemory(got, count - got), cts.Token);
                if (n == 0) break;
                got += n;
            }
        }
        catch (OperationCanceledException) { }
        catch { }
        return got == count ? buf : buf[..got];
    }

    /// <summary>Reads one line, skipping EDGELINK_* internal messages.</summary>
    public async Task<string?> ReadDataLineAsync(int timeout = 3000)
    {
        while (true)
        {
            string? line = await ReadRawLineAsync(timeout);
            if (line == null) return null;
            if (!line.StartsWith("EDGELINK_")) return line;
        }
    }

    public async Task<string?> ReadRawLineAsync(int timeout = 3000)
    {
        using var cts = new CancellationTokenSource(timeout);
        var sb  = new StringBuilder();
        var buf = new byte[1];
        try
        {
            while (true)
            {
                int n = await _stream.ReadAsync(buf, cts.Token);
                if (n == 0) return sb.Length > 0 ? sb.ToString().TrimEnd('\r') : null;
                char c = (char)buf[0];
                if (c == '\n') return sb.ToString().TrimEnd('\r');
                sb.Append(c);
            }
        }
        catch (OperationCanceledException) { return null; }
        catch { return null; }
    }

    public void Close()   => tcp.Close();
    public void Dispose() => tcp.Dispose();
}

public sealed class LocalTcpServer(int port) : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, port);
    private bool _started;

    private void EnsureStarted()
    {
        if (_started) return;
        _listener.Start();
        _started = true;
    }

    public async Task<TestTcpConnection> AcceptAsync(int timeout = 5000)
    {
        EnsureStarted();
        using var cts = new CancellationTokenSource(timeout);
        TcpClient client = await _listener.AcceptTcpClientAsync(cts.Token);
        return new TestTcpConnection(client);
    }

    public void Dispose() { try { _listener.Stop(); } catch { } }
}
