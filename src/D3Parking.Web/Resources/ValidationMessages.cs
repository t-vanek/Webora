using System.Globalization;
using System.Resources;

namespace D3Parking.Web.Resources;

/// <summary>
/// Strongly-typed accessor over ValidationMessages.resx for use with DataAnnotations'
/// ErrorMessageResourceType/ErrorMessageResourceName (Blazor's validator resolves these per culture).
/// </summary>
public static class ValidationMessages
{
    private static readonly ResourceManager ResourceManager =
        new("D3Parking.Web.Resources.ValidationMessages", typeof(ValidationMessages).Assembly);

    public static string Email_Required => Get();
    public static string Email_Invalid => Get();
    public static string Password_Required => Get();
    public static string Password_Length => Get();
    public static string ConfirmPassword_Mismatch => Get();
    public static string Phone_Required => Get();
    public static string Phone_Invalid => Get();
    public static string Code_Required => Get();

    private static string Get([System.Runtime.CompilerServices.CallerMemberName] string key = "") =>
        ResourceManager.GetString(key, CultureInfo.CurrentUICulture) ?? key;
}
