using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace WechatDraft.Core;

public sealed class LocalRepository
{
    private readonly string _connectionString;
    private readonly ISecretProtector _protector;
    public LocalRepository(string folder, ISecretProtector protector)
    {
        Directory.CreateDirectory(folder);
        _protector = protector;
        _connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(folder, "drafts.db") }.ToString();
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS settings (id INTEGER PRIMARY KEY CHECK(id=1), payload TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS contacts (id TEXT PRIMARY KEY, scope TEXT NOT NULL, payload TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS tasks (id TEXT PRIMARY KEY, scope TEXT NOT NULL, created TEXT NOT NULL, payload TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS tasks_created ON tasks(created DESC);
            CREATE INDEX IF NOT EXISTS tasks_scope_created_id ON tasks(scope, created DESC, id DESC);
            CREATE TABLE IF NOT EXISTS sent_greetings (id TEXT PRIMARY KEY, scope TEXT NOT NULL, created TEXT NOT NULL, payload TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS sent_greetings_created ON sent_greetings(created DESC);
            PRAGMA user_version=1;
            """;
        command.ExecuteNonQuery();
    }
    private SqliteConnection Open() { var db = new SqliteConnection(_connectionString); db.Open(); return db; }
    private void Write(string sql, params (string, object?)[] values)
    {
        using var db = Open(); using var command = db.CreateCommand(); command.CommandText = sql;
        foreach (var (key, value) in values) command.Parameters.AddWithValue(key, value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }
    public AppSettings LoadSettings()
    {
        using var db = Open(); using var command = db.CreateCommand(); command.CommandText = "SELECT payload FROM settings WHERE id=1";
        var settings = command.ExecuteScalar() is string data ? Decode<AppSettings>(data) : new AppSettings();
        return ApiProfileSettings.Normalize(settings);
    }
    public void SaveSettings(AppSettings settings) => Write("INSERT INTO settings VALUES(1,$p) ON CONFLICT(id) DO UPDATE SET payload=$p", ("$p", Encode(settings)));
    public void SaveContact(Contact contact)
    {
        contact.UpdatedAt = DateTimeOffset.UtcNow;
        Write("INSERT INTO contacts VALUES($id,$scope,$p) ON CONFLICT(id) DO UPDATE SET payload=$p", ("$id", contact.Id), ("$scope", contact.AccountScope), ("$p", Encode(contact)));
    }
    public List<Contact> Contacts(string scope) => Read<Contact>("SELECT payload FROM contacts WHERE scope=$scope ORDER BY rowid DESC", scope);
    public Contact? FindContactByName(string scope, string displayName, bool isDemo = false)
    {
        var name = displayName.Trim();
        if (name.Length == 0) return null;
        return Contacts(scope).Where(x => x.IsDemo == isDemo &&
                (string.Equals(x.RemarkName?.Trim(), name, StringComparison.Ordinal) ||
                 string.Equals(x.DisplayName.Trim(), name, StringComparison.Ordinal)))
            .OrderByDescending(x => x.Confirmed)
            .ThenByDescending(x => string.Equals(x.RemarkName?.Trim(), name, StringComparison.Ordinal))
            .ThenByDescending(x => x.UpdatedAt)
            .ThenBy(x => x.Id, StringComparer.Ordinal)
            .FirstOrDefault();
    }
    public void DeleteContact(string id, bool deleteHistory)
    {
        // Keep historical snapshots unless deletion of associated history was explicitly requested.
        if (deleteHistory) foreach (var item in History(null, 100000).Where(t => t.ContactId == id)) DeleteTask(item.Id);
        if (deleteHistory) foreach (var item in Read<SentGreetingRecord>("SELECT payload FROM sent_greetings", null).Where(t => t.ContactId == id)) DeleteSentGreeting(item.Id);
        Write("DELETE FROM contacts WHERE id=$id", ("$id", id));
    }
    public void SaveTask(TaskRecord task) => Write("INSERT INTO tasks VALUES($id,$scope,$created,$p) ON CONFLICT(id) DO UPDATE SET payload=$p",
        ("$id", task.Id), ("$scope", task.AccountScope), ("$created", task.StartedAt.UtcDateTime.ToString("O")), ("$p", Encode(task)));
    public List<TaskRecord> History(string? scope, int limit = 200) => Read<TaskRecord>(scope is null
        ? "SELECT payload FROM tasks ORDER BY created DESC LIMIT " + Math.Clamp(limit, 1, 100000)
        : "SELECT payload FROM tasks WHERE scope=$scope ORDER BY created DESC LIMIT " + Math.Clamp(limit, 1, 100000), scope);
    public IReadOnlyList<string> RecentGreetings(string scope, string? contactId, string displayName, string scene,
        bool isDemo, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(displayName)) return [];
        using var db = Open(); using var command = db.CreateCommand();
        command.CommandText = "SELECT payload FROM tasks WHERE scope=$scope ORDER BY created DESC,id DESC LIMIT 200";
        command.Parameters.AddWithValue("$scope", scope);
        using var reader = command.ExecuteReader();
        var result = new List<string>(); var seen = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var task = Decode<TaskRecord>(reader.GetString(0));
            var sameContact = !string.IsNullOrEmpty(contactId) && !string.IsNullOrEmpty(task.ContactId)
                ? task.ContactId == contactId : task.DisplayName.Trim() == displayName.Trim();
            if (!sameContact || task.IsDemo != isDemo || task.Scene.Trim() != scene.Trim() ||
                task.GenerationStatus != "成功" || string.IsNullOrWhiteSpace(task.Greeting)) continue;
            if (seen.Add(GreetingDiversity.Normalize(task.Greeting))) result.Add(task.Greeting);
            if (result.Count == 5) break;
        }
        return result;
    }
    public HistoryBatch ReadHistoryBatch(string scope, HistoryCursor? before, int limit, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        limit = Math.Clamp(limit, 1, 200);
        using var db = Open(); using var command = db.CreateCommand();
        command.CommandText = before is null
            ? "SELECT id,created,payload FROM tasks WHERE scope=$scope ORDER BY created DESC,id DESC LIMIT $limit"
            : "SELECT id,created,payload FROM tasks WHERE scope=$scope AND (created < $created OR (created=$created AND id < $id)) ORDER BY created DESC,id DESC LIMIT $limit";
        command.Parameters.AddWithValue("$scope", scope); command.Parameters.AddWithValue("$limit", limit + 1);
        if (before is not null)
        { command.Parameters.AddWithValue("$created", before.Created); command.Parameters.AddWithValue("$id", before.Id); }
        using var reader = command.ExecuteReader();
        var records = new List<TaskRecord>(); HistoryCursor? cursor = before;
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (records.Count == limit) return new(records, cursor, true);
            records.Add(Decode<TaskRecord>(reader.GetString(2)));
            cursor = new(reader.GetString(1), reader.GetString(0));
        }
        return new(records, cursor, false);
    }
    public void DeleteTask(string id) => Write("DELETE FROM tasks WHERE id=$id", ("$id", id));
    public void ClearHistory() => Write("DELETE FROM tasks");
    public void SaveSentGreeting(SentGreetingRecord record) => Write(
        "INSERT INTO sent_greetings VALUES($id,$scope,$created,$p) ON CONFLICT(id) DO UPDATE SET payload=$p",
        ("$id", record.Id), ("$scope", record.AccountScope), ("$created", record.ObservedAt.UtcDateTime.ToString("O")), ("$p", Encode(record)));
    public SentGreetingRecord? FindSentGreeting(string id)
    {
        using var db = Open(); using var command = db.CreateCommand();
        command.CommandText = "SELECT payload FROM sent_greetings WHERE id=$id"; command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar() is string payload ? Decode<SentGreetingRecord>(payload) : null;
    }
    public bool UpdateSentGreeting(SentGreetingRecord record)
    {
        using var db = Open(); using var command = db.CreateCommand();
        command.CommandText = "UPDATE sent_greetings SET payload=$p WHERE id=$id AND scope=$scope";
        command.Parameters.AddWithValue("$p", Encode(record)); command.Parameters.AddWithValue("$id", record.Id);
        command.Parameters.AddWithValue("$scope", record.AccountScope); return command.ExecuteNonQuery() == 1;
    }
    public List<SentGreetingRecord> SentGreetings(string scope, int limit = 200) => Read<SentGreetingRecord>(
        "SELECT payload FROM sent_greetings WHERE scope=$scope ORDER BY created DESC LIMIT " + Math.Clamp(limit, 1, 100000), scope);
    public void DeleteSentGreeting(string id) => Write("DELETE FROM sent_greetings WHERE id=$id", ("$id", id));
    public void ClearSentGreetings(string scope) => Write("DELETE FROM sent_greetings WHERE scope=$scope", ("$scope", scope));
    public void Cleanup(int retentionDays)
    {
        if (retentionDays > 0) Write("DELETE FROM tasks WHERE created < $cutoff", ("$cutoff", DateTimeOffset.UtcNow.AddDays(-retentionDays).UtcDateTime.ToString("O")));
        if (retentionDays > 0) Write("DELETE FROM sent_greetings WHERE created < $cutoff", ("$cutoff", DateTimeOffset.UtcNow.AddDays(-retentionDays).UtcDateTime.ToString("O")));
    }
    public void RecoverInterrupted()
    {
        foreach (var record in Read<SentGreetingRecord>("SELECT payload FROM sent_greetings", null).Where(x => x.Status == "核对中"))
        {
            record.Status = "待确认"; record.Detail = "上次核对中断，未恢复截图、请求或微信操作。"; SaveSentGreeting(record);
        }
        foreach (var task in History(null, 100000).Where(t => t.CompletedAt is null))
        {
            task.FillStatus = task.AppendAttemptId is null ? "中断" : "预填结果待确认";
            task.GenerationStatus = task.GenerationStatus == "成功" ? "成功" : "中断";
            task.Error = "上次运行意外结束，未恢复微信输入动作。";
            task.CompletedAt = DateTimeOffset.UtcNow; SaveTask(task);
        }
    }
    private List<T> Read<T>(string sql, string? scope)
    {
        using var db = Open(); using var command = db.CreateCommand(); command.CommandText = sql;
        if (scope is not null) command.Parameters.AddWithValue("$scope", scope);
        using var reader = command.ExecuteReader(); var result = new List<T>();
        while (reader.Read()) result.Add(Decode<T>(reader.GetString(0)));
        return result;
    }
    private string Encode<T>(T value) => _protector.Protect(JsonSerializer.Serialize(value, JsonDefaults.Options));
    private T Decode<T>(string value) => JsonSerializer.Deserialize<T>(_protector.Unprotect(value), JsonDefaults.Options)
        ?? throw new InvalidDataException("本地数据无法读取。");
}
