using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace AwgEasy.Control;

/// <param name="Kind">Stable and dotted, like event kinds: <c>failover.switched</c>, <c>node.blocked</c>.</param>
public sealed record Notification(string Kind, string Title, string Message, string? NodeId, DateTimeOffset At);

/// <summary>One place a notification can go. Each is independent: one failing never stops the rest.</summary>
public interface INotificationChannel
{
    string Name { get; }

    Task SendAsync(Notification notification, CancellationToken cancellationToken);
}

/// <summary>
/// Tells the operator what the panel saw or did without being asked. Until now everything that
/// happened was in the event log, which is read after the fact; a node getting blocked at 3am is
/// something that should find the operator instead.
///
/// Delivery is best effort and never in the way. A notification that cannot be sent is logged
/// and dropped - failover must not wait on, let alone fail because of, a chat API.
/// </summary>
public sealed class Notifier(IEnumerable<INotificationChannel> channels, ILogger<Notifier> logger)
{
    private readonly INotificationChannel[] _channels = [.. channels];

    public string[] ChannelNames => [.. _channels.Select(channel => channel.Name)];

    public bool IsConfigured => _channels.Length > 0;

    public async Task SendAsync(Notification notification, CancellationToken cancellationToken)
    {
        foreach (var channel in _channels)
        {
            try
            {
                await channel.SendAsync(notification, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(exception, "Could not deliver {Kind} through {Channel}.", notification.Kind, channel.Name);
            }
        }
    }
}

/// <summary>POSTs the notification as JSON. The shape is the <see cref="Notification"/> record.</summary>
public sealed class WebhookNotificationChannel(IHttpClientFactory httpFactory, string url) : INotificationChannel
{
    public string Name => "webhook";

    public async Task SendAsync(Notification notification, CancellationToken cancellationToken)
    {
        using var http = httpFactory.CreateClient(NotificationChannels.HttpClientName);
        using var response = await http.PostAsJsonAsync(url, notification, ControlJsonContext.Default.Notification, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}

public sealed class TelegramNotificationChannel(IHttpClientFactory httpFactory, string botToken, string chatId) : INotificationChannel
{
    public string Name => "telegram";

    public async Task SendAsync(Notification notification, CancellationToken cancellationToken)
    {
        using var http = httpFactory.CreateClient(NotificationChannels.HttpClientName);
        // Plain text, no parse mode: node names and error messages are not ours to escape, and a
        // stray underscore in Markdown mode turns a failover alert into a 400.
        var message = new TelegramMessage(chatId, $"{notification.Title}\n\n{notification.Message}", DisableWebPagePreview: true);
        using var response = await http.PostAsJsonAsync(
            $"https://api.telegram.org/bot{botToken}/sendMessage",
            message,
            ControlJsonContext.Default.TelegramMessage,
            cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}

internal sealed record TelegramMessage(
    [property: JsonPropertyName("chat_id")] string ChatId,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("disable_web_page_preview")] bool DisableWebPagePreview);

public static class NotificationChannels
{
    public const string HttpClientName = "notifications";

    /// <summary>Registers a channel for each destination the environment configures, and none otherwise.</summary>
    public static IServiceCollection AddNotifications(this IServiceCollection services, NotificationOptions options)
    {
        services.AddHttpClient(HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(10));

        if (!string.IsNullOrWhiteSpace(options.WebhookUrl))
        {
            services.AddSingleton<INotificationChannel>(provider =>
                new WebhookNotificationChannel(provider.GetRequiredService<IHttpClientFactory>(), options.WebhookUrl));
        }

        if (options.TelegramConfigured)
        {
            services.AddSingleton<INotificationChannel>(provider =>
                new TelegramNotificationChannel(provider.GetRequiredService<IHttpClientFactory>(), options.TelegramBotToken!, options.TelegramChatId!));
        }

        services.AddSingleton<Notifier>();
        return services;
    }
}
