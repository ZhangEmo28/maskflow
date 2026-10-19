using MaskFlow.Api.Infrastructure.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

[Route("api/auth")]
[Tags("Auth")]
public sealed class AuthController : ControllerBase
{
    private readonly MaskFlowStore store;
    private readonly LoginRateLimiter loginRateLimiter;
    private readonly IWebHostEnvironment environment;
    private readonly ILogger<AuthController> logger;

    public AuthController(MaskFlowStore store, LoginRateLimiter loginRateLimiter, IWebHostEnvironment environment, ILogger<AuthController> logger)
    {
        this.store = store;
        this.loginRateLimiter = loginRateLimiter;
        this.environment = environment;
        this.logger = logger;
    }

    string ClientIp => HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    [AllowAnonymous]
    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] RegisterRequest? request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
        {
            return BadRequest(new { detail = "Email and password are required." });
        }

        if (request.Password.Length < 8)
        {
            return BadRequest(new { detail = "Password must be at least 8 characters." });
        }

        var result = await store.RegisterAsync(request.Email, request.Password, request.Username, HttpContext);
        return result is null ? Conflict(new { detail = "Email already exists." }) : Ok(result);
    }

    [AllowAnonymous]
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest? request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
        {
            return BadRequest(new { detail = "Email and password are required." });
        }

        // Two windows: per IP + account slows targeted guessing, per IP alone slows password spraying.
        var rateKey = $"{ClientIp}:{request.Email.Trim().ToLowerInvariant()}";
        var ipKey = $"ip:{ClientIp}";
        if (loginRateLimiter.IsLocked(rateKey, out var retryAfterSeconds)
            || loginRateLimiter.IsLocked(ipKey, out retryAfterSeconds))
        {
            Response.Headers.RetryAfter = retryAfterSeconds.ToString();
            return StatusCode(429, new { detail = $"Too many login attempts. Retry after {retryAfterSeconds} seconds." });
        }

        var result = await store.LoginAsync(request.Email, request.Password, HttpContext);
        if (result is null)
        {
            loginRateLimiter.RecordFailure(rateKey);
            loginRateLimiter.RecordFailure(ipKey, LoginRateLimiter.MaxAttemptsPerIp);
            return Unauthorized(new { detail = "Invalid email or password." });
        }

        loginRateLimiter.RecordSuccess(rateKey);
        return Ok(result);
    }

    [AllowAnonymous]
    [HttpPost("forgot-password")]
    public IActionResult ForgotPassword([FromBody] ForgotPasswordRequest? request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Email))
        {
            return BadRequest(new { detail = "Email is required." });
        }

        var rateKey = $"forgot:{ClientIp}:{request.Email.Trim().ToLowerInvariant()}";
        if (loginRateLimiter.IsLocked(rateKey, out var retryAfterSeconds))
        {
            Response.Headers.RetryAfter = retryAfterSeconds.ToString();
            return StatusCode(429, new { detail = $"Too many reset attempts. Retry after {retryAfterSeconds} seconds." });
        }

        loginRateLimiter.RecordFailure(rateKey);
        var token = store.CreatePasswordResetToken(request.Email);

        // The reset code is a password substitute: it must never travel back in the HTTP response.
        if (token is not null && environment.IsDevelopment())
        {
            logger.LogWarning("Development password reset code for {Email}: {Token}", request.Email.Trim().ToLowerInvariant(), token);
        }

        // Always acknowledge the request to avoid email enumeration.
        return Ok(new
        {
            ok = true,
            message = environment.IsDevelopment()
                ? "如果该邮箱已注册，重置码已生成。开发环境请在 API 服务日志中查看重置码。"
                : "如果该邮箱已注册，重置码将发送到该邮箱，30 分钟内有效。"
        });
    }

    [AllowAnonymous]
    [HttpPost("reset-password")]
    public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordRequest? request)
    {
        if (request is null
            || string.IsNullOrWhiteSpace(request.Email)
            || string.IsNullOrWhiteSpace(request.Token)
            || string.IsNullOrWhiteSpace(request.NewPassword))
        {
            return BadRequest(new { detail = "Email, token and newPassword are required." });
        }

        if (request.NewPassword.Length < 8)
        {
            return BadRequest(new { detail = "Password must be at least 8 characters." });
        }

        var rateKey = $"reset:{ClientIp}:{request.Email.Trim().ToLowerInvariant()}";
        if (loginRateLimiter.IsLocked(rateKey, out var retryAfterSeconds))
        {
            Response.Headers.RetryAfter = retryAfterSeconds.ToString();
            return StatusCode(429, new { detail = $"Too many reset attempts. Retry after {retryAfterSeconds} seconds." });
        }

        var changed = await store.ResetPasswordWithTokenAsync(request.Email, request.Token, request.NewPassword);
        if (!changed)
        {
            loginRateLimiter.RecordFailure(rateKey);
            return BadRequest(new { detail = "重置码无效或已过期，请重新获取。" });
        }

        loginRateLimiter.RecordSuccess(rateKey);
        return Ok(new { ok = true, message = "密码已重置，请使用新密码登录。" });
    }
}
