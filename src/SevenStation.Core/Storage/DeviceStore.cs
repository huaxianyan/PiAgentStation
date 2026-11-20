using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace SevenStation.Core.Storage;

/// <summary>
/// 一台一台手机的令牌：每台设备一个，作废一台不影响别的。
/// 主令牌（token.txt）仍然管桌面客户端和 Control，这里只管新增的设备。
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class DeviceStore
{
    private readonly string _databasePath;
    private readonly object _gate = new();

    public DeviceStore(string databasePath)
    {
        _databasePath = databasePath;
        lock (_gate)
        {
            using var connection = Connect();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS devices (
                    id TEXT PRIMARY KEY,
                    name TEXT NOT NULL,
                    token TEXT NOT NULL UNIQUE,
                    created_at INTEGER NOT NULL,
                    last_seen_at INTEGER,
                    revoked_at INTEGER
                );
                """;
            command.ExecuteNonQuery();
        }
    }

    public string DatabasePath => _databasePath;

    private SqliteConnection Connect()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString());
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=3000;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    /// <summary>新建一台设备，返回它自己的令牌（只在这一刻出现，之后只给看前几位）。</summary>
    public JsonObject Add(string name)
    {
        var trimmed = string.IsNullOrWhiteSpace(name) ? "新设备" : name.Trim();
        if (trimmed.Length > 40) trimmed = trimmed[..40];

        var id = "d-" + Guid.NewGuid().ToString("N")[..10];
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();

        lock (_gate)
        {
            using var connection = Connect();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO devices (id, name, token, created_at)
                VALUES ($id, $name, $token, $now);
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$name", trimmed);
            command.Parameters.AddWithValue("$token", token);
            command.Parameters.AddWithValue("$now", now);
            command.ExecuteNonQuery();
        }

        return new JsonObject
        {
            ["id"] = id,
            ["name"] = trimmed,
            ["token"] = token,
            ["createdAt"] = now,
        };
    }

    public JsonArray List()
    {
        var items = new JsonArray();
        lock (_gate)
        {
            using var connection = Connect();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT id, name, token, created_at, last_seen_at, revoked_at FROM devices ORDER BY created_at DESC;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                items.Add(new JsonObject
                {
                    ["id"] = reader.GetString(0),
                    ["name"] = reader.GetString(1),
                    ["tokenHint"] = Head(reader.GetString(2)),
                    ["createdAt"] = reader.GetInt64(3),
                    ["lastSeenAt"] = reader.IsDBNull(4) ? null : reader.GetInt64(4),
                    ["revokedAt"] = reader.IsDBNull(5) ? null : reader.GetInt64(5),
                });
            }
        }

        return items;
    }

    /// <summary>作废。已经作废的再点一次也返回成功，界面上不用纠结。</summary>
    public bool Revoke(string id)
    {
        lock (_gate)
        {
            using var connection = Connect();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE devices SET revoked_at = $now WHERE id = $id AND revoked_at IS NULL;";
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$now", DateTimeOffset.Now.ToUnixTimeMilliseconds());
            command.ExecuteNonQuery();
        }

        return true;
    }

    /// <summary>这个令牌是不是某台还没作废的设备；是就把它的最后在线时间刷一下。</summary>
    public string? Match(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;

        lock (_gate)
        {
            using var connection = Connect();
            using var find = connection.CreateCommand();
            find.CommandText = "SELECT id FROM devices WHERE token = $token AND revoked_at IS NULL;";
            find.Parameters.AddWithValue("$token", token);
            var id = find.ExecuteScalar() as string;
            if (id is null) return null;

            using var touch = connection.CreateCommand();
            touch.CommandText = "UPDATE devices SET last_seen_at = $now WHERE id = $id;";
            touch.Parameters.AddWithValue("$id", id);
            touch.Parameters.AddWithValue("$now", DateTimeOffset.Now.ToUnixTimeMilliseconds());
            touch.ExecuteNonQuery();
            return id;
        }
    }

    /// <summary>界面上只看得到前 8 位，够认出是哪台，不够拼回去。</summary>
    public static string Head(string token)
    {
        return token.Length <= 8 ? token : token[..8] + "…";
    }

    /// <summary>本机的私网地址，Control 拿去给人挑（家里 Wi-Fi、以后 EasyTier 的虚拟网段都在这）。</summary>
    public static JsonArray Addresses(int port)
    {
        var items = new JsonArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            foreach (var info in nic.GetIPProperties().UnicastAddresses)
            {
                if (info.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (IPAddress.IsLoopback(info.Address)) continue;
                if (!IsPrivate(info.Address)) continue;

                var address = info.Address.ToString();
                if (!seen.Add(address)) continue;
                items.Add(new JsonObject
                {
                    ["address"] = address,
                    ["name"] = nic.Name,
                    ["url"] = $"http://{address}:{port}",
                });
            }
        }

        return items;
    }

    private static bool IsPrivate(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        if (bytes.Length != 4) return false;

        // 10/8、172.16/12、192.168/16：家庭局域网和常见虚拟网卡都在这里面。
        if (bytes[0] == 10) return true;
        if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
        if (bytes[0] == 192 && bytes[1] == 168) return true;
        return false;
    }
}
