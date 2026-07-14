using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Coflnet.Sky.EventBroker.Models;
using FirebaseAdmin;
using FirebaseAdmin.Messaging;
using Google.Apis.Auth.OAuth2;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Coflnet.Sky.EventBroker.Services;

/// <summary>
/// Result of attempting to deliver a push notification
/// </summary>
public enum PushResult
{
    /// <summary>Firebase accepted the message</summary>
    Sent,
    /// <summary>The token is not valid (anymore), the target should be disabled</summary>
    InvalidToken,
    /// <summary>Sending failed for a reason that may resolve itself, the target should be kept</summary>
    TemporaryFailure,
    /// <summary>No firebase credentials are configured, nothing was sent</summary>
    NotConfigured
}

/// <summary>
/// Sends push notifications via the firebase cloud messaging HTTP v1 api.
/// The legacy `fcm/send` api this used to call was decommissioned by google and always returns 404.
/// </summary>
public class FirebasePushService
{
    private readonly ILogger<FirebasePushService> logger;
    private readonly FirebaseMessaging messaging;

    /// <summary>
    /// Whether credentials were found and push notifications can be sent
    /// </summary>
    public bool IsConfigured => messaging != null;

    /// <summary>
    /// Creates a new instance of <see cref="FirebasePushService"/>
    /// </summary>
    public FirebasePushService(IConfiguration config, ILogger<FirebasePushService> logger)
    {
        this.logger = logger;
        var credential = LoadCredential(config);
        if (credential == null)
        {
            logger.LogWarning("No firebase service account configured (FIREBASE_SERVICE_ACCOUNT or FIREBASE_SERVICE_ACCOUNT_FILE), push notifications are disabled");
            return;
        }
        var app = FirebaseApp.GetInstance("skyEventBroker")
            ?? FirebaseApp.Create(new AppOptions { Credential = credential }, "skyEventBroker");
        messaging = FirebaseMessaging.GetMessaging(app);
    }

    private GoogleCredential LoadCredential(IConfiguration config)
    {
        var json = config["FIREBASE_SERVICE_ACCOUNT"];
        var path = config["FIREBASE_SERVICE_ACCOUNT_FILE"];
        try
        {
            if (!string.IsNullOrWhiteSpace(json))
                return CredentialFactory.FromJson<ServiceAccountCredential>(json).ToGoogleCredential();
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                return CredentialFactory.FromFile<ServiceAccountCredential>(path).ToGoogleCredential();
        }
        catch (Exception e)
        {
            logger.LogError(e, "Could not load firebase service account, push notifications are disabled");
        }
        return null;
    }

    /// <summary>
    /// Sends a single notification to a device token
    /// </summary>
    /// <param name="token">the fcm registration token of the device</param>
    /// <param name="notification">what to send</param>
    public async Task<PushResult> SendAsync(string token, FirebaseNotification notification)
    {
        if (!IsConfigured)
            return PushResult.NotConfigured;
        var message = new Message
        {
            // the sdk suggests Fid, but the targets we store are registration tokens handed out by the client sdk, not installation ids
#pragma warning disable CS0618
            Token = token,
#pragma warning restore CS0618
            Notification = new Notification
            {
                Title = notification.title,
                Body = notification.body,
                ImageUrl = AbsoluteOrNull(notification.icon)
            },
            Data = notification.data ?? new Dictionary<string, string>(),
            Webpush = new WebpushConfig
            {
                FcmOptions = new WebpushFcmOptions { Link = AbsoluteOrNull(notification.click_action) },
                Notification = new WebpushNotification { Icon = AbsoluteOrNull(notification.icon) }
            }
        };
        try
        {
            var id = await messaging.SendAsync(message);
            logger.LogInformation("sent push notification {id} for {title}", id, notification.title);
            return PushResult.Sent;
        }
        catch (FirebaseMessagingException e)
        {
            // only these mean the token itself is dead, everything else may work again later
            if (e.MessagingErrorCode == MessagingErrorCode.Unregistered
                || e.MessagingErrorCode == MessagingErrorCode.InvalidArgument
                || e.MessagingErrorCode == MessagingErrorCode.SenderIdMismatch)
            {
                logger.LogInformation("push token rejected by firebase ({code}), disabling target", e.MessagingErrorCode);
                return PushResult.InvalidToken;
            }
            logger.LogError(e, "Sending push notification failed temporarily ({code})", e.MessagingErrorCode);
            return PushResult.TemporaryFailure;
        }
        catch (Exception e)
        {
            logger.LogError(e, "Sending push notification failed");
            return PushResult.TemporaryFailure;
        }
    }

    /// <summary>
    /// Firebase rejects the whole message when a link/image is not an absolute url
    /// </summary>
    private static string AbsoluteOrNull(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var parsed) && (parsed.Scheme == Uri.UriSchemeHttps || parsed.Scheme == Uri.UriSchemeHttp))
            return url;
        return null;
    }
}
