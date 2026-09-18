using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using PumpStationMonitor.Core.Services;
using PumpStationMonitor.Data.Security;

namespace PumpStationMonitor.Data;

// 集中处理站点配置、用户、历史和报警的 SQLite 读写。
// 用参数化 SQL 和短生命周期连接隔离存储细节，便于上层复用。
// 需要一致性的批量操作放在事务内，历史记录保留独立生命周期。
public sealed class StationRepository
{
    private readonly string connectionString;
    public string DatabasePath
    {
        get;
    }

    public StationRepository(string path)
    {
        DatabasePath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            ForeignKeys = true,
            DefaultTimeout = 10
        }.ToString();
        DefaultTypeMap.MatchNamesWithUnderscores = true;
    }

    public async Task<SqliteConnection> OpenAsync(CancellationToken token = default)
    {
        var db = new SqliteConnection(connectionString);
        try
        {
            await db.OpenAsync(token);
            return db;
        }
        catch
        {
            await db.DisposeAsync();
            throw;
        }
    }

    public async Task InitializeAsync(
        CommunicationSettings defaults,
        CancellationToken token = default
    )
    {
        await using var db = await OpenAsync(token);
        using var stream = typeof(StationRepository).Assembly.GetManifestResourceStream(
            "PumpStationMonitor.Data.schema.sql")!;
        using var reader = new StreamReader(stream);
        string schema = await reader.ReadToEndAsync(token);
        var createSchema = new CommandDefinition(schema, cancellationToken: token);
        await db.ExecuteAsync(createSchema);
        await using var transaction = await db.BeginTransactionAsync(token);
        if (await db.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM devices", transaction: transaction)
            == 0)
        {
            await db.ExecuteAsync(
                "INSERT INTO devices VALUES (@Id,@Name,@UnitId)",
                Seed.Devices,
                transaction
            );
            await db.ExecuteAsync(TagInsert, Seed.Tags, transaction);
            await db.ExecuteAsync(RuleInsert, Seed.Rules, transaction);
        }

        if (await db.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM users", transaction: transaction)
            == 0)
        {
            await db.ExecuteAsync(
                "INSERT INTO users VALUES (@Username,@PasswordHash,@Role,1)",
                new[] {
                    new UserRow {
                        Username = "admin",
                        PasswordHash = PasswordHasher.Hash("Admin123!"),
                        Role = "Admin"
                    },
                    new UserRow {
                        Username = "operator",
                        PasswordHash = PasswordHasher.Hash("Operator123!"),
                        Role = "Operator"
                    }
                },
                transaction
            );
        }

        await db.ExecuteAsync(
            "INSERT OR IGNORE INTO comm_settings VALUES (1,@Json)",
            new
            {
                Json = JsonSerializer.Serialize(defaults)
            },
            transaction
        );
        await transaction.CommitAsync(token);
    }

    private const string TagInsert = ("INSERT INTO tags(id,device_id,name,function,address,unit,scale,offset,"
            + "bit,data_type,byte_order,enabled) VALUES (@Id,@DeviceId,@Name,"
            + "@Function,@Address,@Unit,@Scale,@Offset,@Bit,@DataType,@ByteOrder,"
            + "@Enabled)");
    private const string RuleInsert = ("INSERT INTO alarm_rules(id,tag_id,kind,limit_value,deadband,delay_ms,"
            + "severity,enabled) VALUES (@Id,@TagId,@Kind,@LimitValue,@Deadband,"
            + "@DelayMs,@Severity,@Enabled)");
    public async Task<List<DeviceRow>> DevicesAsync()
    {
        await using var db = await OpenAsync();
        var rows = await db.QueryAsync<DeviceRow>("SELECT * FROM devices ORDER BY id");
        return rows.ToList();
    }

    public async Task<List<TagRow>> TagsAsync()
    {
        await using var db = await OpenAsync();
        var rows = await db.QueryAsync<TagRow>("SELECT * FROM tags ORDER BY device_id,address,bit");
        return rows.ToList();
    }

    public async Task<List<RuleRow>> RulesAsync()
    {
        await using var db = await OpenAsync();
        var rows = await db.QueryAsync<RuleRow>("SELECT * FROM alarm_rules ORDER BY tag_id,id");
        return rows.ToList();
    }

    public async Task<List<UserRow>> UsersAsync()
    {
        await using var db = await OpenAsync();
        var rows = await db.QueryAsync<UserRow>("SELECT * FROM users ORDER BY username");
        return rows.ToList();
    }

    public async Task<UserRow?> UserAsync(string name)
    {
        await using var db = await OpenAsync();
        return await db.QuerySingleOrDefaultAsync<UserRow>("SELECT * FROM users WHERE username=@name", new
        {
            name
        });
    }

    public async Task<CommunicationSettings> SettingsAsync()
    {
        await using var db = await OpenAsync();
        string json = await db.QuerySingleAsync<string>("SELECT json FROM comm_settings WHERE id=1");
        var settings = JsonSerializer.Deserialize<CommunicationSettings>(json);
        return settings!;
    }

    public async Task SaveSettingsAsync(CommunicationSettings settings)
    {
        await using var db = await OpenAsync();
        string json = JsonSerializer.Serialize(settings);
        await db.ExecuteAsync("UPDATE comm_settings SET json=@json WHERE id=1", new
        {
            json
        });
    }

    public async Task SaveTagAsync(TagRow tag)
    {
        await using var db = await OpenAsync();
        string sql = TagInsert
            + (" ON CONFLICT(id) DO UPDATE SET device_id=excluded.device_id,"
                + "name=excluded.name,function=excluded.function,"
                + "address=excluded.address,unit=excluded.unit,scale=excluded.scale,"
                + "offset=excluded.offset,bit=excluded.bit,data_type=excluded.data_type,"
                + "byte_order=excluded.byte_order,enabled=excluded.enabled");
        await db.ExecuteAsync(sql, tag);
    }

    public async Task DeleteTagAsync(string id)
    {
        await using var db = await OpenAsync();
        await using var tx = await db.BeginTransactionAsync();
        const string activeAlarmSql = "SELECT COUNT(*) FROM alarm_events WHERE tag_id=@id AND recovered_at IS NULL";
        long activeCount = await db.ExecuteScalarAsync<long>(activeAlarmSql, new
        {
            id
        }, tx);
        if (activeCount > 0)
        {
            throw new InvalidOperationException("点位仍有活动报警，请恢复后删除");
        }

        await db.ExecuteAsync("DELETE FROM alarm_rules WHERE tag_id=@id", new
        {
            id
        }, tx);
        await db.ExecuteAsync("DELETE FROM tags WHERE id=@id", new
        {
            id
        }, tx);
        await tx.CommitAsync();
    }

    public async Task SaveRuleAsync(RuleRow rule)
    {
        await using var db = await OpenAsync();
        string sql = RuleInsert
            + (" ON CONFLICT(id) DO UPDATE SET tag_id=excluded.tag_id,"
                + "kind=excluded.kind,limit_value=excluded.limit_value,"
                + "deadband=excluded.deadband,delay_ms=excluded.delay_ms,"
                + "severity=excluded.severity,enabled=excluded.enabled");
        await db.ExecuteAsync(sql, rule);
    }

    public async Task DeleteRuleAsync(string id)
    {
        await using var db = await OpenAsync();
        const string activeAlarmSql = "SELECT COUNT(*) FROM alarm_events WHERE rule_id=@id AND recovered_at IS NULL";
        long activeCount = await db.ExecuteScalarAsync<long>(activeAlarmSql, new
        {
            id
        });
        if (activeCount > 0)
        {
            throw new InvalidOperationException("规则仍有活动报警");
        }

        await db.ExecuteAsync("DELETE FROM alarm_rules WHERE id=@id", new
        {
            id
        });
    }

    public async Task SaveUserAsync(UserRow user)
    {
        await using var db = await OpenAsync();
        const string sql = ("INSERT INTO users VALUES (@Username,@PasswordHash,@Role,@Enabled) ON "
                + "CONFLICT(username) DO UPDATE SET password_hash=excluded.password_hash,"
                + "role=excluded.role,enabled=excluded.enabled");
        await db.ExecuteAsync(sql, user);
    }

    public async Task DeleteUserAsync(string username)
    {
        await using var db = await OpenAsync();
        await db.ExecuteAsync("DELETE FROM users WHERE username=@username", new
        {
            username
        });
    }

    public async Task InsertHistoryAsync(
        IReadOnlyList<HistoryRow> rows,
        CancellationToken token = default
    )
    {
        if (rows.Count == 0)
        {
            return;
        }

        await using var db = await OpenAsync(token);
        await using var tx = await db.BeginTransactionAsync(token);
        const string sql = "INSERT INTO history(tag_id,device_id,timestamp,value,quality) "
            + "VALUES (@TagId,@DeviceId,@Timestamp,@Value,@Quality)";
        var command = new CommandDefinition(sql, rows, tx, cancellationToken: token);
        await db.ExecuteAsync(command);
        await tx.CommitAsync(token);
    }

    public async Task<List<HistoryRow>> QueryHistoryAsync(
        string? device,
        string? tag,
        long from,
        long to,
        int limit = 10000,
        CancellationToken token = default
    )
    {
        if (from > to || limit is < 1 or > 500000)
        {
            throw new ArgumentException("查询时间或数量无效");
        }

        await using var db = await OpenAsync(token);
        string where = "timestamp>=@from AND timestamp<=@to";
        if (!string.IsNullOrWhiteSpace(tag))
        {
            where += " AND tag_id=@tag";
        }

        if (!string.IsNullOrWhiteSpace(device))
        {
            where += " AND device_id=@device";
        }

        string sql = $"SELECT * FROM history WHERE {where} ORDER BY timestamp,id LIMIT @limit";
        var parameters = new
        {
            device,
            tag,
            from,
            to,
            limit
        };
        var query = new CommandDefinition(sql, parameters, cancellationToken: token);
        var rows = await db.QueryAsync<HistoryRow>(query);
        return rows.ToList();
    }

    public async Task<long> HistoryCountAsync()
    {
        await using var db = await OpenAsync();
        return await db.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM history");
    }

    public async Task CleanupAsync(int days, CancellationToken token = default)
    {
        await using var db = await OpenAsync(token);
        // 分块删除让写队列能在清理间隙获得数据库写锁。
        int removed;
        do
        {
            const string sql = "DELETE FROM history WHERE id IN "
                + "(SELECT id FROM history WHERE timestamp<@cutoff LIMIT 5000)";
            long cutoff = DateTimeOffset.UtcNow.AddDays(-days).ToUnixTimeMilliseconds();
            var command = new CommandDefinition(sql, new
            {
                cutoff
            }, cancellationToken: token);
            removed = await db.ExecuteAsync(command);
        }
        while (removed > 0 && !token.IsCancellationRequested);
    }

    public async Task<List<AlarmRow>> AlarmsAsync(
        long from = 0,
        long to = long.MaxValue,
        bool activeOnly = false
    )
    {
        await using var db = await OpenAsync();
        string activeFilter = activeOnly ? " AND recovered_at IS NULL" : "";
        string sql = "SELECT * FROM alarm_events WHERE raised_at BETWEEN @from AND @to" + activeFilter
            + " ORDER BY raised_at DESC LIMIT 10000";
        var rows = await db.QueryAsync<AlarmRow>(sql, new
        {
            from,
            to
        });
        return rows.ToList();
    }

    public async Task RaiseAlarmAsync(AlarmRow row)
    {
        await using var db = await OpenAsync();
        const string sql = ("INSERT OR IGNORE INTO alarm_events(id,rule_id,tag_id,name,severity,"
                + "raised_at,value) VALUES (@Id,@RuleId,@TagId,@Name,@Severity,@RaisedAt,"
                + "@Value)");
        await db.ExecuteAsync(sql, row);
    }

    public async Task RecoverAlarmAsync(string id, long time)
    {
        await using var db = await OpenAsync();
        await db.ExecuteAsync(
            "UPDATE alarm_events SET recovered_at=@time WHERE id=@id AND recovered_at IS NULL",
            new
            {
                id,
                time
            }
        );
    }

    public async Task<int> AcknowledgeAsync(string id, string user)
    {
        await using var db = await OpenAsync();
        const string sql = ("UPDATE alarm_events SET acknowledged_at=@time,acknowledged_by=@user "
                + "WHERE id=@id AND acknowledged_at IS NULL");
        return await db.ExecuteAsync(
            sql,
            new
            {
                id,
                user,
                time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            }
        );
    }
}
