using System.Globalization;
using System.Resources;

namespace Webora.Web.Resources;

/// <summary>
/// Strongly-typed accessor over ValidationMessages.resx for use with DataAnnotations'
/// ErrorMessageResourceType/ErrorMessageResourceName (Blazor's validator resolves these per culture).
/// </summary>
public static class ValidationMessages
{
    private static readonly ResourceManager ResourceManager =
        new("Webora.Web.Resources.ValidationMessages", typeof(ValidationMessages).Assembly);

    public static string Email_Required => Get();
    public static string Email_Invalid => Get();
    public static string Password_Required => Get();
    public static string Password_Length => Get();
    public static string ConfirmPassword_Mismatch => Get();

    private static string Get([System.Runtime.CompilerServices.CallerMemberName] string key = "") =>
        ResourceManager.GetString(key, CultureInfo.CurrentUICulture) ?? key;
}
