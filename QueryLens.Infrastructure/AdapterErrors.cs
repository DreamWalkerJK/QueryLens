using System.Data.Common;
using System.Net.Sockets;
using System.Security.Authentication;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using Npgsql;

namespace QueryLens.Infrastructure;

public enum AdapterErrorKind { Configuration, Authentication, Tls, PermissionDenied, Timeout, Connection, Unsupported, SamplingInterval, SecretUnavailable, DataQuality }

/// <summary>No provider message/inner exception is retained: these can contain SQL or credentials.</summary>
public sealed class AdapterException(AdapterErrorKind kind, string message, string? providerCode = null) : Exception(message)
{
    public AdapterErrorKind Kind { get; } = kind;
    public string? ProviderCode { get; } = providerCode;
}

public static class AdapterErrors
{
    public static AdapterException Classify(Exception exception)
    {
        if (exception is AdapterException known) return known;
        var code = exception switch { PostgresException pg => pg.SqlState, MySqlException my => my.Number.ToString(), SqlException sql => sql.Number.ToString(), _ => null };
        var kind = exception switch
        {
            PostgresException pg when pg.SqlState is "28P01" or "28000" => AdapterErrorKind.Authentication,
            PostgresException pg when pg.SqlState == "42501" => AdapterErrorKind.PermissionDenied,
            PostgresException pg when pg.SqlState is "42P01" or "42703" or "42883" or "55000" => AdapterErrorKind.Unsupported,
            PostgresException pg when pg.SqlState == "57014" => AdapterErrorKind.Timeout,
            MySqlException my when my.Number is 1045 or 1698 => AdapterErrorKind.Authentication,
            MySqlException my when my.Number is 1044 or 1142 or 1227 or 1370 => AdapterErrorKind.PermissionDenied,
            MySqlException my when my.Number is 1146 or 1054 or 1193 => AdapterErrorKind.Unsupported,
            MySqlException my when my.Number is 3024 or 1969 or -1 => AdapterErrorKind.Timeout,
            SqlException sql when sql.Number is 18456 or 18452 => AdapterErrorKind.Authentication,
            SqlException sql when sql.Number is 229 or 297 or 300 or 916 => AdapterErrorKind.PermissionDenied,
            SqlException sql when sql.Number is 207 or 208 => AdapterErrorKind.Unsupported,
            SqlException sql when sql.Number == -2 => AdapterErrorKind.Timeout,
            TimeoutException => AdapterErrorKind.Timeout,
            AuthenticationException => AdapterErrorKind.Tls,
            ArgumentException => AdapterErrorKind.Configuration,
            _ when HasInner<AuthenticationException>(exception) => AdapterErrorKind.Tls,
            _ when HasInner<TimeoutException>(exception) => AdapterErrorKind.Timeout,
            DbException or SocketException or IOException => AdapterErrorKind.Connection,
            _ => AdapterErrorKind.DataQuality
        };
        var message = kind switch
        {
            AdapterErrorKind.Authentication => "身份验证失败。请检查认证方式、用户名及本机保存的密码。",
            AdapterErrorKind.Tls => "TLS 协商或证书校验失败。请核对主机名、服务器证书及受信任 CA；不要关闭证书校验来连接生产环境。",
            AdapterErrorKind.PermissionDenied => "连接已建立，但诊断对象访问被拒绝。请由 DBA 检查此能力列出的最小权限。",
            AdapterErrorKind.Timeout => "数据库操作超时。请检查网络和服务器负载，或调整连接超时后重试。",
            AdapterErrorKind.Unsupported => "此数据库版本、扩展配置或产品形态不提供需要的诊断对象。请查看能力概览及接入文档。",
            AdapterErrorKind.Configuration => "连接配置无效。请检查主机、端口、数据库、用户名、认证方式及超时。",
            AdapterErrorKind.Connection => "无法建立或维持数据库连接。请检查主机、端口、网络、防火墙和数据库监听状态。",
            _ => "数据库返回了不能安全解析的诊断数据。请核对产品版本和采集方式。"
        };
        return new AdapterException(kind, message, code);
    }

    private static bool HasInner<T>(Exception exception) where T : Exception
    {
        for (var current = exception; current is not null; current = current.InnerException!) if (current is T) return true;
        return false;
    }
}
