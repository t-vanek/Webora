namespace D3Parking.Infrastructure.Email;

/// <summary>Supplies an OAuth2 access token for SMTP XOAUTH2 authentication.</summary>
public interface ISmtpAccessTokenProvider
{
    Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default);
}
