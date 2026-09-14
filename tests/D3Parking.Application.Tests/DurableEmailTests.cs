using D3Parking.Application.Abstractions.Email;
using D3Parking.Domain.Email;
using D3Parking.Infrastructure.Email;
using D3Parking.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace D3Parking.Application.Tests;

[TestFixture, NonParallelizable]
public sealed class DurableEmailTests
{
    private DbContextOptions<D3ParkingDbContext> _options = null!;
    private string _keys = null!;
    private readonly Clock _clock = new();
    private static EmailMessage Message => new() { To = "recipient@test.local", Subject = "Reset password",
        HtmlBody = "<a href='https://example.test/reset?token=secret-reset-token'>Reset</a>", TextBody = "secret-reset-token" };
    private IDataProtectionProvider Protection() => DataProtectionProvider.Create(new DirectoryInfo(_keys));
    private DurableEmailSender Sender() => new(new Factory(_options), Protection(), _clock);
    private EmailDeliveryDispatcher Dispatcher(IEmailTransport transport) =>
        new(new Factory(_options), Protection(), transport, _clock, NullLogger<EmailDeliveryDispatcher>.Instance);

    [SetUp]
    public async Task SetUp()
    {
        var sql = Environment.GetEnvironmentVariable("ConnectionStrings__SqlServer");
        if (string.IsNullOrWhiteSpace(sql)) Assert.Ignore("Requires SQL Server and creates a unique temporary database.");
        _options = new DbContextOptionsBuilder<D3ParkingDbContext>().UseSqlServer(new SqlConnectionStringBuilder(sql)
        { InitialCatalog = $"D3Parking_DurableEmail_{Guid.NewGuid():N}" }.ConnectionString).Options;
        _keys = Path.Combine(Path.GetTempPath(), $"d3parking-mail-keys-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_keys);
        _clock.Now = new DateTimeOffset(2026, 9, 14, 10, 0, 0, TimeSpan.Zero);
        await using var db = new D3ParkingDbContext(_options);
        await db.Database.EnsureCreatedAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_options is null) return;
        await using var db = new D3ParkingDbContext(_options);
        await db.Database.EnsureDeletedAsync();
        if (_keys is not null && Directory.Exists(_keys)) Directory.Delete(_keys, true);
    }

    private async Task<EmailDelivery> Row()
    {
        await using var db = new D3ParkingDbContext(_options);
        return await db.EmailDeliveries.AsNoTracking().SingleAsync();
    }

    [Test]
    public async Task Committed_encrypted_email_survives_sender_and_key_provider_recreation()
    {
        await Sender().SendAsync(Message);
        var row = await Row();
        Assert.That(row.ProtectedPayload, Does.Not.Contain("secret-reset-token").And.Not.Contain("recipient@test.local"));
        var transport = new Transport();
        Assert.That(await Dispatcher(transport).DeliverDueAsync(), Is.EqualTo(1));
        Assert.That(transport.Messages.Single().HtmlBody, Is.EqualTo(Message.HtmlBody));
        row = await Row();
        Assert.That(row.Status, Is.EqualTo(EmailDeliveryStatus.Sent));
        Assert.That(row.ProtectedPayload, Is.Null);
        Assert.That(row.CompletedAtUtc, Is.Not.Null);
        Assert.That(await Dispatcher(transport).DeliverDueAsync(), Is.Zero);
    }

    [Test]
    public async Task Failed_SMTP_is_retried_after_backoff_with_the_same_message_identity()
    {
        await Sender().SendAsync(Message);
        var failed = new Transport { OnSend = (_, _) => throw new IOException("secret-reset-token") };
        await Dispatcher(failed).DeliverDueAsync();
        var row = await Row();
        Assert.That(row.Status, Is.EqualTo(EmailDeliveryStatus.Pending));
        Assert.That(row.LastError, Is.EqualTo(nameof(IOException)));
        Assert.That(row.NextAttemptUtc, Is.EqualTo(_clock.Now.AddMinutes(1)));
        var succeeding = new Transport();
        Assert.That(await Dispatcher(succeeding).DeliverDueAsync(), Is.Zero);
        _clock.Now = row.NextAttemptUtc;
        await Dispatcher(succeeding).DeliverDueAsync();
        Assert.That(succeeding.Messages.Single().MessageId, Is.EqualTo(failed.Messages.Single().MessageId).And.Not.Null);
        Assert.That((await Row()).Attempts, Is.EqualTo(2));
    }

    [Test]
    public async Task Concurrent_dispatcher_cannot_send_an_actively_leased_row()
    {
        await Sender().SendAsync(Message);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new Transport { OnSend = async (_, _) => { entered.SetResult(); await finish.Task; } };
        var running = Dispatcher(first).DeliverDueAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var other = new Transport();
            Assert.That(await Dispatcher(other).DeliverDueAsync(), Is.Zero);
            Assert.That(other.Messages, Is.Empty);
        }
        finally { finish.TrySetResult(); await running; }
    }

    [Test]
    public async Task Shutdown_leaves_a_durable_lease_that_a_new_worker_can_reclaim()
    {
        await Sender().SendAsync(Message);
        using var stop = new CancellationTokenSource();
        var interrupted = new Transport { OnSend = (_, ct) => { stop.Cancel(); ct.ThrowIfCancellationRequested(); return Task.CompletedTask; } };
        Assert.ThrowsAsync<OperationCanceledException>(() => Dispatcher(interrupted).DeliverDueAsync(stop.Token));
        var row = await Row();
        Assert.That(row.Status, Is.EqualTo(EmailDeliveryStatus.Pending));
        Assert.That(row.LeaseId, Is.Not.Null);
        var restarted = new Transport();
        Assert.That(await Dispatcher(restarted).DeliverDueAsync(), Is.Zero);
        _clock.Now = row.LeasedUntilUtc!.Value.AddSeconds(1);
        await Dispatcher(restarted).DeliverDueAsync();
        Assert.That((await Row()).Status, Is.EqualTo(EmailDeliveryStatus.Sent));
    }

    [Test]
    public async Task Stale_worker_cannot_overwrite_the_new_owners_success()
    {
        await Sender().SendAsync(Message);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stale = new Transport { OnSend = async (_, _) => { entered.SetResult(); await finish.Task; throw new IOException(); } };
        var running = Dispatcher(stale).DeliverDueAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            _clock.Now += EmailDeliveryDispatcher.LeaseDuration + TimeSpan.FromSeconds(1);
            await Dispatcher(new Transport()).DeliverDueAsync();
        }
        finally { finish.TrySetResult(); await running; }
        Assert.That((await Row()).Status, Is.EqualTo(EmailDeliveryStatus.Sent));
        Assert.That((await Row()).LastError, Is.Null);
    }

    [Test]
    public async Task Repeated_failure_is_terminal_and_sensitive_payload_is_erased()
    {
        await Sender().SendAsync(Message);
        var broken = new Transport { OnSend = (_, _) => throw new IOException("secret-reset-token") };
        for (var attempt = 0; attempt < EmailDeliveryDispatcher.MaxAttempts; attempt++)
        {
            _clock.Now = (await Row()).NextAttemptUtc;
            await Dispatcher(broken).DeliverDueAsync();
        }
        var row = await Row();
        Assert.That(row.Status, Is.EqualTo(EmailDeliveryStatus.Failed));
        Assert.That(row.ProtectedPayload, Is.Null);
        Assert.That(row.LastError, Is.EqualTo(nameof(IOException)));
        Assert.That(await Dispatcher(broken).DeliverDueAsync(), Is.Zero);
    }

    [Test]
    public async Task Expired_emails_are_not_sent_and_only_completed_metadata_is_purged()
    {
        await Sender().SendAsync(Message);
        _clock.Now = (await Row()).ExpiresAtUtc.AddSeconds(1);
        var transport = new Transport();
        await Dispatcher(transport).DeliverDueAsync();
        Assert.That(transport.Messages, Is.Empty);
        Assert.That((await Row()).Status, Is.EqualTo(EmailDeliveryStatus.Failed));
        Assert.That((await Row()).ProtectedPayload, Is.Null);
        _clock.Now = _clock.Now.AddDays(31);
        await Sender().SendAsync(Message); // A fresh pending message must survive retention cleanup.
        Assert.That(await Dispatcher(transport).PurgeCompletedAsync(), Is.EqualTo(1));
        Assert.That((await Row()).Status, Is.EqualTo(EmailDeliveryStatus.Pending));
    }

    [Test]
    public async Task Final_abandoned_attempt_is_closed_after_its_lease_expires()
    {
        await Sender().SendAsync(Message);
        await using var db = new D3ParkingDbContext(_options);
        await db.EmailDeliveries.ExecuteUpdateAsync(s => s.SetProperty(d => d.Attempts, EmailDeliveryDispatcher.MaxAttempts)
            .SetProperty(d => d.LeaseId, Guid.NewGuid())
            .SetProperty(d => d.LeasedUntilUtc, _clock.Now.Add(EmailDeliveryDispatcher.LeaseDuration)));
        var transport = new Transport();
        await Dispatcher(transport).DeliverDueAsync();
        Assert.That((await Row()).Status, Is.EqualTo(EmailDeliveryStatus.Pending));
        _clock.Now += EmailDeliveryDispatcher.LeaseDuration + TimeSpan.FromSeconds(1);
        await Dispatcher(transport).DeliverDueAsync();
        Assert.That((await Row()).Status, Is.EqualTo(EmailDeliveryStatus.Failed));
        Assert.That((await Row()).ProtectedPayload, Is.Null);
        Assert.That(transport.Messages, Is.Empty);
    }

    [Test]
    public async Task Cancellation_before_enqueue_does_not_acknowledge_or_persist_work()
    {
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        Assert.ThrowsAsync<OperationCanceledException>(() => Sender().SendAsync(Message, stop.Token));
        await using var db = new D3ParkingDbContext(_options);
        Assert.That(await db.EmailDeliveries.CountAsync(), Is.Zero);
    }

    [Test]
    public async Task Unavailable_database_makes_enqueue_fail_instead_of_losing_a_message_silently()
    {
        await using var db = new D3ParkingDbContext(_options);
        await db.Database.ExecuteSqlRawAsync("DROP TABLE [EmailDeliveries]");
        Assert.ThrowsAsync<DbUpdateException>(() => Sender().SendAsync(Message));
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; }
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Factory(DbContextOptions<D3ParkingDbContext> options) : IDbContextFactory<D3ParkingDbContext>
    {
        public D3ParkingDbContext CreateDbContext() => new(options);
    }
    private sealed class Transport : IEmailTransport
    {
        public List<EmailMessage> Messages { get; } = [];
        public Func<EmailMessage, CancellationToken, Task>? OnSend { get; init; }
        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
        {
            Messages.Add(message);
            return OnSend?.Invoke(message, cancellationToken) ?? Task.CompletedTask;
        }
    }
}
