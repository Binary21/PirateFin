using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.PirateFin.Notifications;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.PirateFin;
using MediaBrowser.Model.PirateFin;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.PirateFin;

/// <summary>
/// Emailed temporary passwords: sent only to users with an email, work once within 30 minutes, and leave the user
/// having to choose a new password.
/// </summary>
public sealed class TemporaryPasswordServiceTests : IDisposable
{
    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "piratefin-temp-passwords-" + Guid.NewGuid().ToString("N"));
    private readonly User _user = new("viewer", "default", "default");
    private readonly Mock<IUserManager> _userManager = new();
    private readonly Mock<IUserNotificationSettings> _settings = new();
    private readonly Mock<IEmailSender> _email = new();
    private readonly SettableTimeProvider _time = new(new DateTime(2026, 10, 25, 20, 0, 0, DateTimeKind.Utc));
    private readonly TemporaryPasswordService _service;
    private string? _sentBody;

    public TemporaryPasswordServiceTests()
    {
        _userManager.Setup(u => u.GetUserByName("viewer")).Returns(_user);
        _settings.Setup(s => s.Get(_user.Id)).Returns(new NotificationSettings { Email = "viewer@example.com" });
        _email.SetupGet(e => e.IsConfigured).Returns(true);
        _email.Setup(e => e.SendAsync("viewer@example.com", It.IsAny<PirateFinEmail>(), It.IsAny<CancellationToken>()))
            .Callback((string _, PirateFinEmail email, CancellationToken _) => _sentBody = email.Text)
            .Returns(Task.CompletedTask);

        _service = new TemporaryPasswordService(
            _userManager.Object,
            _settings.Object,
            _email.Object,
            Mock.Of<IEmailAssets>(),
            Mock.Of<IApplicationPaths>(p => p.DataPath == _dataPath),
            _time,
            NullLogger<TemporaryPasswordService>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataPath))
        {
            Directory.Delete(_dataPath, true);
        }
    }

    private string SentPassword()
    {
        // The plain text version shows the password indented on its own line.
        var line = Assert.Single(_sentBody!.Split(Environment.NewLine), l => l.StartsWith("    ", StringComparison.Ordinal));
        return line.Trim();
    }

    [Fact]
    public async Task TemporaryPassword_WorksOnce_ThenANewPasswordIsRequired()
    {
        Assert.True(await _service.SendAsync("viewer", CancellationToken.None));
        var password = SentPassword();

        Assert.False(_service.TryRedeem(_user.Id, "wrong"));
        Assert.False(_service.IsChangeRequired(_user.Id));

        // Dashes and case don't matter.
        Assert.True(_service.TryRedeem(_user.Id, password.Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant()));
        Assert.True(_service.IsChangeRequired(_user.Id));
        Assert.False(_service.TryRedeem(_user.Id, password));

        _service.ClearChangeRequired(_user.Id);
        Assert.False(_service.IsChangeRequired(_user.Id));
    }

    [Fact]
    public async Task TemporaryPassword_Expires()
    {
        await _service.SendAsync("viewer", CancellationToken.None);
        _time.Advance(TemporaryPasswordService.Lifetime);

        Assert.False(_service.TryRedeem(_user.Id, SentPassword()));
    }

    [Fact]
    public async Task Send_WithoutEmailAddress_SendsNothing()
    {
        _settings.Setup(s => s.Get(_user.Id)).Returns(new NotificationSettings());

        Assert.False(await _service.SendAsync("viewer", CancellationToken.None));
        Assert.False(await _service.SendAsync("nobody", CancellationToken.None));
        _email.Verify(e => e.SendAsync(It.IsAny<string>(), It.IsAny<PirateFinEmail>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Send_AskedAgainRightAway_DoesNotSendAnotherEmail()
    {
        await _service.SendAsync("viewer", CancellationToken.None);
        var first = SentPassword();
        Assert.True(await _service.SendAsync("viewer", CancellationToken.None));

        _email.Verify(e => e.SendAsync(It.IsAny<string>(), It.IsAny<PirateFinEmail>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.True(_service.TryRedeem(_user.Id, first));
    }

    [Fact]
    public async Task Send_EmailFails_CanAskAgainRightAway()
    {
        _email.Setup(e => e.SendAsync(It.IsAny<string>(), It.IsAny<PirateFinEmail>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new System.Net.Mail.SmtpException("down"));
        await Assert.ThrowsAsync<System.Net.Mail.SmtpException>(() => _service.SendAsync("viewer", CancellationToken.None));

        _email.Setup(e => e.SendAsync(It.IsAny<string>(), It.IsAny<PirateFinEmail>(), It.IsAny<CancellationToken>()))
            .Callback((string _, PirateFinEmail email, CancellationToken _) => _sentBody = email.Text)
            .Returns(Task.CompletedTask);
        Assert.True(await _service.SendAsync("viewer", CancellationToken.None));
        Assert.True(_service.TryRedeem(_user.Id, SentPassword()));
    }

    [Fact]
    public void CreatePassword_IsThreeGroupsOfFourHex()
    {
        var password = TemporaryPasswordService.CreatePassword();

        Assert.Matches("^[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}$", password);
        Assert.NotEqual(password, Enumerable.Range(0, 5).Select(_ => TemporaryPasswordService.CreatePassword()).First());
    }

    private sealed class SettableTimeProvider(DateTime utcNow) : TimeProvider
    {
        private DateTime _utcNow = utcNow;

        public void Advance(TimeSpan by) => _utcNow += by;

        public override DateTimeOffset GetUtcNow() => new(_utcNow);
    }
}
