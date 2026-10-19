using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

public interface IEmailSender
{
    bool IsConfigured { get; }
    Task SendAsync(string to, string subject, string body, CancellationToken cancellationToken = default);
}

public sealed record SmtpSettings(string Host, int Port, string? Username, string? Password, string From, string Security)
{
    // MASKFLOW_SMTP_HOST enables mail; MASKFLOW_SMTP_SECURITY: starttls (default) | ssl | none.
    public static SmtpSettings? FromEnvironment()
    {
        var host = Environment.GetEnvironmentVariable("MASKFLOW_SMTP_HOST");
        if (string.IsNullOrWhiteSpace(host)) return null;
        var port = int.TryParse(Environment.GetEnvironmentVariable("MASKFLOW_SMTP_PORT"), out var value) ? value : 587;
        var from = Environment.GetEnvironmentVariable("MASKFLOW_SMTP_FROM");
        var username = Environment.GetEnvironmentVariable("MASKFLOW_SMTP_USERNAME");
        return new SmtpSettings(
            host.Trim(),
            port,
            username,
            Environment.GetEnvironmentVariable("MASKFLOW_SMTP_PASSWORD"),
            string.IsNullOrWhiteSpace(from) ? username ?? "maskflow@localhost" : from,
            (Environment.GetEnvironmentVariable("MASKFLOW_SMTP_SECURITY") ?? "starttls").Trim().ToLowerInvariant());
    }
}

public sealed class SmtpEmailSender(SmtpSettings settings) : IEmailSender
{
    public bool IsConfigured => true;

    public async Task SendAsync(string to, string subject, string body, CancellationToken cancellationToken = default)
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(settings.From));
        message.To.Add(MailboxAddress.Parse(to));
        message.Subject = subject;
        message.Body = new TextPart("plain") { Text = body };

        using var client = new SmtpClient();
        var security = settings.Security switch
        {
            "ssl" => SecureSocketOptions.SslOnConnect,
            "none" => SecureSocketOptions.None,
            _ => SecureSocketOptions.StartTls
        };
        await client.ConnectAsync(settings.Host, settings.Port, security, cancellationToken);
        if (!string.IsNullOrEmpty(settings.Username))
        {
            await client.AuthenticateAsync(settings.Username, settings.Password ?? "", cancellationToken);
        }

        await client.SendAsync(message, cancellationToken);
        await client.DisconnectAsync(true, cancellationToken);
    }
}

// Used when SMTP is not configured: nothing leaves the server. Message bodies are only logged in
// Development because they can contain secrets such as password reset codes.
public sealed class LoggingEmailSender(ILogger<LoggingEmailSender> logger, IHostEnvironment environment) : IEmailSender
{
    public bool IsConfigured => false;

    public Task SendAsync(string to, string subject, string body, CancellationToken cancellationToken = default)
    {
        if (environment.IsDevelopment())
        {
            logger.LogWarning("SMTP not configured; development mail to {To}: {Subject}\n{Body}", to, subject, body);
        }
        else
        {
            logger.LogWarning("SMTP not configured; mail to {To} ({Subject}) was not sent. Set MASKFLOW_SMTP_HOST.", to, subject);
        }

        return Task.CompletedTask;
    }
}

// All user-facing mail in one place. Failures are logged, never surfaced: a failed send must not
// reveal whether an account exists or break the request that triggered it.
public sealed class AccountMailer(IEmailSender sender, ILogger<AccountMailer> logger)
{
    static string PublicUrl => (Environment.GetEnvironmentVariable("MASKFLOW_PUBLIC_URL") ?? "http://127.0.0.1:3000").TrimEnd('/');

    public bool CanDeliver => sender.IsConfigured;

    public Task SendPasswordResetAsync(string email, string code) => SendAsync(email,
        "MaskFlow 密码重置码",
        $"""
        你好，

        你的 MaskFlow 密码重置码是：{code}
        30 分钟内有效。打开 {PublicUrl}/auth.html?mode=reset 输入邮箱、重置码和新密码即可完成重置。

        如果不是你本人操作，请忽略这封邮件，你的密码不会改变。
        """);

    public Task SendProjectInviteAsync(string email, string inviter, string projectName, string role, bool hasAccount) => SendAsync(email,
        $"{inviter} 邀请你参与 MaskFlow 项目「{projectName}」",
        hasAccount
            ? $"""
              你好，

              {inviter} 已把你加入项目「{projectName}」，角色：{role}。
              登录 {PublicUrl}/annotate.html 即可开始。
              """
            : $"""
              你好，

              {inviter} 邀请你以「{role}」身份参与项目「{projectName}」。
              请用这个邮箱在 {PublicUrl}/auth.html?mode=register 注册，注册后会自动加入项目。
              """);

    public Task SendBatchFinishedAsync(string email, string projectName, string status, BatchProgress progress) => SendAsync(email,
        $"MaskFlow 批量标注{(status == "completed" ? "已完成" : "已中止")}：{projectName}",
        $"""
        项目「{projectName}」的批量标注{(status == "completed" ? "已完成" : "已中止")}。
        处理 {progress.Processed} 张：成功 {progress.Succeeded}，失败 {progress.Failed}。
        {(progress.LastError is null ? "" : $"最近错误：{progress.LastError}")}

        打开 {PublicUrl}/annotate.html 查看结果。
        """);

    public Task SendTrainingFinishedAsync(string email, string projectName, TrainingRun run)
    {
        var outcome = run.Status switch { "completed" => "已完成", "cancelled" => "已取消", _ => "失败" };
        var headline = MaskFlowStore.TrainingHeadline(run);
        return SendAsync(email,
            $"MaskFlow 模型训练{outcome}：{run.Name}",
            $"""
            项目「{projectName}」的训练「{run.Name}」{outcome}。
            基础权重：{run.Request.BaseWeight}，训练 {run.Epoch}/{run.Request.Epochs} 轮，{run.ImageCount} 张图片。
            {(headline.Count == 0 ? "" : "验证集指标：" + string.Join("，", headline.Select(x => $"{x.Key} {x.Value:0.###}")))}
            {(run.ModelId is null ? "" : "训练好的模型已加入项目模型列表，可直接用于自动标注。")}
            {(run.Error is null ? "" : $"说明：{run.Error}")}

            打开 {PublicUrl}/train.html 查看曲线、评估结果和下载权重。
            """);
    }

    async Task SendAsync(string to, string subject, string body)
    {
        try
        {
            await sender.SendAsync(to, subject, body);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send mail {Subject} to {To}", subject, to);
        }
    }
}
