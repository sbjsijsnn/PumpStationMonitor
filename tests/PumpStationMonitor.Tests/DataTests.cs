using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PumpStationMonitor.Core.Models;
using PumpStationMonitor.Core.Services;
using PumpStationMonitor.Data;
using PumpStationMonitor.Data.Security;
using PumpStationMonitor.Data.Services;
using PumpStationMonitor.Simulator;
namespace PumpStationMonitor.Tests;

public class DataTests
{
    private sealed class Rig : IAsyncDisposable
    {
        public string Folder { get; } = Path.Combine(Path.GetTempPath(), "PumpStationTests", Guid.NewGuid().ToString("N"));
        public StationRepository Repository { get; }
        public StationState Station { get; } = new();
        public ModbusServer Server { get; }
        public HistoryWriter Writer { get; }
        public StationRuntime Runtime { get; }
        public SessionService Session { get; }
        public StationFacade Facade { get; }
        public Rig()
        {
            Repository = new(Path.Combine(Folder, "test.db")); Server = new(Station, NullLogger<ModbusServer>.Instance);
            Writer = new(Repository, NullLogger<HistoryWriter>.Instance); Runtime = new(Repository, Writer, NullLoggerFactory.Instance, NullLogger<StationRuntime>.Instance);
            Session = new(Repository, NullLogger<SessionService>.Instance); Facade = new(Repository, Runtime, Session, NullLogger<StationFacade>.Instance);
        }
        public async Task StartAsync()
        {
            Server.Start(0); await Repository.InitializeAsync(new() { Port = Server.Port, TimeoutMs = 100, Retries = 0 });
            await Runtime.ReloadAsync(); await Writer.StartAsync(default);
        }
        public async ValueTask DisposeAsync() { await Writer.StopAsync(default); Writer.Dispose(); await Runtime.DisposeAsync(); await Server.DisposeAsync(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); }
    }
    [Fact]
    public async Task SeedIsIdempotentAndPasswordsAreHashed()
    {
        await using var rig = new Rig(); await rig.StartAsync(); await rig.Repository.InitializeAsync(new());
        Assert.Equal(4, (await rig.Repository.DevicesAsync()).Count); Assert.Equal(23, (await rig.Repository.TagsAsync()).Count); Assert.Equal(6, (await rig.Repository.RulesAsync()).Count);
        var admin = await rig.Repository.UserAsync("ADMIN"); Assert.NotNull(admin); Assert.DoesNotContain("Admin123!", admin.PasswordHash); Assert.True(PasswordHasher.Verify("Admin123!", admin.PasswordHash)); Assert.False(PasswordHasher.Verify("wrong", admin.PasswordHash));
    }
    [Fact]
    public void PasswordSaltAndCsvEscaping()
    {
        Assert.NotEqual(PasswordHasher.Hash("Password123"), PasswordHasher.Hash("Password123"));
        Assert.Equal("\"'=SUM(A1)\"", StationFacade.CsvCell("=SUM(A1)")); Assert.Equal("\"a\"\"b,c\"", StationFacade.CsvCell("a\"b,c"));
    }
    [Fact]
    public async Task OperatorCannotMutateOrControlButCanAcknowledge()
    {
        await using var rig = new Rig(); await rig.StartAsync(); Assert.True(await rig.Session.LoginAsync("operator", "Operator123!"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => rig.Facade.ControlAsync(1, true, "Coil"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => rig.Facade.SaveSettingsAsync(new()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => rig.Facade.SaveTagAsync(new() { Id = "extra" }));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => rig.Facade.SaveRuleAsync(new() { Id = "extra" }));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => rig.Facade.SaveUserAsync("other", "Password123", "Admin", true));
        rig.Station.Command("fault 1"); rig.Station.Update(0); await rig.Runtime.PollAsync(default);
        var alarm = Assert.Single(await rig.Repository.AlarmsAsync(activeOnly: true)); await rig.Facade.AcknowledgeAsync(alarm.Id);
        Assert.Equal("operator", Assert.Single(await rig.Repository.AlarmsAsync()).AcknowledgedBy);
    }
    [Fact]
    public async Task AlarmSurvivesReloadAndRecoveryKeepsAcknowledgement()
    {
        await using var rig = new Rig(); await rig.StartAsync(); await rig.Session.LoginAsync("admin", "Admin123!");
        rig.Station.Command("fault 1"); rig.Station.Update(0); await rig.Runtime.PollAsync(default);
        var raised = Assert.Single(await rig.Repository.AlarmsAsync(activeOnly: true)); await rig.Facade.AcknowledgeAsync(raised.Id);
        await rig.Runtime.ReloadAsync(); await rig.Runtime.PollAsync(default); Assert.Single(await rig.Repository.AlarmsAsync());
        rig.Station.Command("recover"); rig.Station.Update(0); await rig.Runtime.PollAsync(default);
        var recovered = Assert.Single(await rig.Repository.AlarmsAsync()); Assert.NotNull(recovered.RecoveredAt); Assert.NotNull(recovered.AcknowledgedAt); Assert.Equal(raised.Id, recovered.Id);
    }
    [Fact]
    public async Task OfflineMarksQualityAndRecoveryRestoresValues()
    {
        await using var rig = new Rig(); await rig.StartAsync(); await rig.Runtime.PollAsync(default); Assert.All(rig.Runtime.Snapshot.Values, v => Assert.Equal(DataQuality.Good, v.Quality));
        rig.Station.Command("offline"); await rig.Runtime.PollAsync(default); Assert.All(rig.Runtime.Snapshot.Values, v => Assert.Equal(DataQuality.Bad, v.Quality));
        rig.Station.Command("recover"); await rig.Runtime.PollAsync(default); Assert.All(rig.Runtime.Snapshot.Values, v => Assert.Equal(DataQuality.Good, v.Quality));
    }
    [Fact]
    public async Task ShutdownDrainsHistoryAndQueryFiltersTimeDeviceAndTag()
    {
        await using var rig = new Rig(); await rig.StartAsync(); await rig.Runtime.PollAsync(default); await rig.Writer.StopAsync(default);
        Assert.Equal(23, await rig.Repository.HistoryCountAsync()); var rows = await rig.Repository.QueryHistoryAsync("sump", "level", 0, long.MaxValue); Assert.Single(rows); Assert.Equal("Good", rows[0].Quality);
        Assert.Empty(await rig.Repository.QueryHistoryAsync("pump1", "level", 0, long.MaxValue)); Assert.Empty(await rig.Repository.QueryHistoryAsync(null, "level", 0, 1));
    }
    [Fact]
    public async Task RetentionDeletesOnlyOldHistory()
    {
        await using var rig = new Rig(); await rig.StartAsync(); long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await rig.Repository.InsertHistoryAsync([new() { TagId = "level", DeviceId = "sump", Timestamp = now - 40L * 86400000, Value = 1 }, new() { TagId = "level", DeviceId = "sump", Timestamp = now, Value = 2 }]);
        await rig.Repository.CleanupAsync(30); Assert.Equal(2, Assert.Single(await rig.Repository.QueryHistoryAsync(null, null, 0, long.MaxValue)).Value);
    }
    [Fact]
    public async Task AdminCanEditTagsRulesUsersAndControlBothModes()
    {
        await using var rig = new Rig(); await rig.StartAsync(); await rig.Session.LoginAsync("admin", "Admin123!");
        await rig.Facade.SaveTagAsync(new() { Id = "custom", DeviceId = "pump1", Name = "测试点", Address = 100 }); Assert.Contains(await rig.Repository.TagsAsync(), t => t.Id == "custom");
        await rig.Facade.SaveRuleAsync(new() { Id = "custom_rule", TagId = "custom", LimitValue = 1000 }); Assert.Contains(await rig.Repository.RulesAsync(), r => r.Id == "custom_rule");
        await rig.Facade.DeleteRuleAsync("custom_rule"); await rig.Facade.DeleteTagAsync("custom");
        await rig.Facade.SaveUserAsync("worker", "Password123", "Operator", true); Assert.NotNull(await rig.Repository.UserAsync("worker")); await rig.Facade.DeleteUserAsync("worker");
        await rig.Facade.ControlAsync(1, true, "Coil"); rig.Station.Update(0); await rig.Runtime.PollAsync(default); Assert.Equal(1, rig.Runtime.Snapshot.Values.Single(v => v.Tag.Id == "pump1_run").Value);
        await rig.Facade.ControlAsync(1, false, "Register"); rig.Station.Update(0); await rig.Runtime.PollAsync(default); Assert.Equal(0, rig.Runtime.Snapshot.Values.Single(v => v.Tag.Id == "pump1_run").Value);
    }
    [Fact]
    public async Task InvalidConfigAndLastAdminAreRejected()
    {
        await using var rig = new Rig(); await rig.StartAsync(); await rig.Session.LoginAsync("admin", "Admin123!");
        await Assert.ThrowsAsync<ArgumentException>(() => rig.Facade.SaveSettingsAsync(new() { PollIntervalMs = 0 }));
        await Assert.ThrowsAsync<ArgumentException>(() => rig.Facade.SaveTagAsync(new() { Id = "invalid", Address = 65535, DataType = "Float32" }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Facade.SaveUserAsync("admin", "", "Operator", true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Facade.DeleteUserAsync("admin"));
        rig.Station.Command("fault 1"); rig.Station.Update(0); await rig.Runtime.PollAsync(default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Facade.DeleteTagAsync("pump1_fault"));
    }
    [Fact]
    public async Task ExportIncludesConfirmedAndRecoveredAlarm()
    {
        await using var rig = new Rig(); await rig.StartAsync(); await rig.Session.LoginAsync("operator", "Operator123!");
        rig.Station.Command("fault 2"); rig.Station.Update(0); await rig.Runtime.PollAsync(default); var alarm = Assert.Single(await rig.Repository.AlarmsAsync());
        rig.Station.Command("recover"); rig.Station.Update(0); await rig.Runtime.PollAsync(default); await rig.Facade.AcknowledgeAsync(alarm.Id);
        string path = Path.Combine(rig.Folder, "alarms.csv"); await rig.Facade.ExportAlarmsAsync(path, 0, long.MaxValue); string csv = await File.ReadAllTextAsync(path); Assert.Contains("operator", csv); Assert.Contains("已恢复 / 已确认", csv);
    }
    [Fact] public async Task FloatSignedAndBitTagsUseConfiguredAddressAndScale()
    {
        await using var rig=new Rig();await rig.StartAsync();await rig.Session.LoginAsync("admin","Admin123!");
        rig.Station.Respond(1,Core.Protocol.ModbusFrames.WriteMultiple(100,[0x42F6,0xE979,0xFFFF]));
        await rig.Facade.SaveTagAsync(new(){Id="float",DeviceId="pump1",Address=100,DataType="Float32",Scale=2,Offset=1});
        await rig.Facade.SaveTagAsync(new(){Id="signed",DeviceId="pump1",Address=102,DataType="Int16"});
        await rig.Runtime.PollAsync(default);
        Assert.Equal(247.912,rig.Runtime.Snapshot.Values.Single(v=>v.Tag.Id=="float").Value,3);
        Assert.Equal(-1,rig.Runtime.Snapshot.Values.Single(v=>v.Tag.Id=="signed").Value);
    }
    [Fact] public async Task WriterFlushesFullBatchBeforeInterval()
    {
        await using var rig=new Rig();await rig.StartAsync();
        for(int i=0;i<200;i++)await rig.Writer.EnqueueAsync(new(){TagId="level",DeviceId="sump",Timestamp=i,Value=i},default);
        using var deadline=new CancellationTokenSource(900);
        while(rig.Writer.Written<200)await Task.Delay(10,deadline.Token);
        Assert.Equal(200,await rig.Repository.HistoryCountAsync());
    }
    [Fact] public async Task InvalidEnumValuesAndDuplicateAdminChangesAreRejected()
    {
        await using var rig=new Rig();await rig.StartAsync();await rig.Session.LoginAsync("admin","Admin123!");
        await Assert.ThrowsAsync<ArgumentException>(()=>rig.Facade.SaveSettingsAsync(new(){Parity="123"}));
        await Assert.ThrowsAsync<ArgumentException>(()=>rig.Facade.SaveRuleAsync(new(){Id="invalid",Kind="99"}));
        await Assert.ThrowsAsync<ArgumentException>(()=>rig.Facade.ControlAsync(1,true,"invalid"));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>rig.Facade.SaveUserAsync(" admin ","","Operator",true));
    }
}
