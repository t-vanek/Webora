using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using D3Parking.Application.Identity;
using D3Parking.Domain.Accounts;
using D3Parking.Domain.Authorization;
using D3Parking.Infrastructure.Identity;
using D3Parking.Infrastructure.Persistence;

namespace D3Parking.Web.Identity;

/// <summary>
/// The SCIM 2.0 Users endpoints Microsoft Entra's provisioning service talks to.
/// </summary>
/// <remarks>
/// Inbound provisioning rather than outbound polling: Entra pushes joiners, movers and leavers as
/// they happen, so nothing here needs an outbound connection, a Graph permission or a stored
/// credential for the tenant. Only the subset Entra actually exercises is implemented — Users, and
/// within them create, read, filter by userName, patch active, and delete. Groups are deliberately
/// absent: roles come from app role claims at sign-in, which is one source of truth instead of two.
/// </remarks>
public static class ScimEndpoints
{
    private const string UserSchema = "urn:ietf:params:scim:schemas:core:2.0:User";
    private const string ListSchema = "urn:ietf:params:scim:api:messages:2.0:ListResponse";
    private const string ErrorSchema = "urn:ietf:params:scim:api:messages:2.0:Error";
    private const string ScimContentType = "application/scim+json";

    public static IEndpointRouteBuilder MapScimApi(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/scim/v2");

        // The bearer token is the entire boundary here, so it is checked before anything else runs
        // and compared in constant time — a timing oracle on a provisioning secret is still a
        // timing oracle.
        group.AddEndpointFilter(async (invocation, next) =>
        {
            var options = (await invocation.HttpContext.RequestServices
                .GetRequiredService<IEntraSettingsService>()
                .GetEffectiveAsync(invocation.HttpContext.RequestAborted)).Scim;

            if (!options.IsConfigured)
            {
                return Results.NotFound();
            }

            var header = invocation.HttpContext.Request.Headers.Authorization.ToString();
            if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                || !FixedTimeEquals(header["Bearer ".Length..].Trim(), options.BearerToken!))
            {
                return Results.Unauthorized();
            }

            return await next(invocation);
        });

        group.MapGet("/Users", ListUsersAsync);
        group.MapGet("/Users/{id}", GetUserAsync);
        group.MapPost("/Users", CreateUserAsync);
        group.MapPatch("/Users/{id}", PatchUserAsync);
        group.MapPut("/Users/{id}", ReplaceUserAsync);
        group.MapDelete("/Users/{id}", DeleteUserAsync);

        // Entra probes this before its first sync to learn what the endpoint supports.
        group.MapGet("/ServiceProviderConfig", () => Results.Json(new
        {
            schemas = new[] { "urn:ietf:params:scim:schemas:core:2.0:ServiceProviderConfig" },
            patch = new { supported = true },
            bulk = new { supported = false, maxOperations = 0, maxPayloadSize = 0 },
            filter = new { supported = true, maxResults = 200 },
            changePassword = new { supported = false },
            sort = new { supported = false },
            etag = new { supported = false },
            authenticationSchemes = new[]
            {
                new { type = "oauthbearertoken", name = "OAuth Bearer Token", primary = true },
            },
        }, contentType: ScimContentType));

        return app;
    }

    /// <summary>
    /// Entra's only filter here is <c>userName eq "…"</c>, used to ask "do you already have this
    /// person" before every create. Anything else returns an empty list rather than a 400, because
    /// a filter this endpoint does not understand must not stall a whole provisioning cycle.
    /// </summary>
    private static async Task<IResult> ListUsersAsync(
        string? filter,
        D3ParkingDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var userName = ParseUserNameFilter(filter);
        if (userName is null)
        {
            return ScimList([]);
        }

        var user = await dbContext.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.UserName == userName || u.Email == userName, cancellationToken);

        return ScimList(user is null ? [] : [ToScim(user)]);
    }

    private static async Task<IResult> GetUserAsync(
        string id,
        D3ParkingDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var user = await FindAsync(dbContext, id, cancellationToken);
        return user is null
            ? ScimError(StatusCodes.Status404NotFound, "User not found.")
            : Results.Json(ToScim(user), contentType: ScimContentType);
    }

    private static async Task<IResult> CreateUserAsync(
        JsonElement payload,
        IExternalDirectoryService directory,
        D3ParkingDbContext dbContext,
        CancellationToken cancellationToken)
    {
        if (!TryReadActive(payload, out var active))
        {
            return ScimError(StatusCodes.Status400BadRequest, "Expected a user object with a boolean active attribute when provided.");
        }
        var identity = ReadIdentity(payload, active);
        if (identity is null)
        {
            return ScimError(StatusCodes.Status400BadRequest, "externalId and userName are required.");
        }

        // Profil, případné propojení účtu a odchod se buď uloží společně, nebo vůbec.
        await using var transaction = await dbContext.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var result = await directory.SyncAsync(identity, cancellationToken);
        if (!result.Succeeded)
        {
            return ScimError(StatusCodes.Status400BadRequest, string.Join(" ", result.Errors));
        }

        if (active is { } requestedActive)
        {
            var changed = await directory.SetActiveAsync(identity.Provider, identity.ObjectId, requestedActive, cancellationToken);
            if (!changed.Succeeded)
            {
                return ScimError(StatusCodes.Status409Conflict, string.Join(" ", changed.Errors));
            }
        }

        var user = await dbContext.Users.AsNoTracking()
            .FirstAsync(u => u.Id == result.UserId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        // Preserve the existing contract: 201 for a new account, 200 for an existing/adopted one.
        // A successful repeated delivery is not a conflict.
        return Results.Json(ToScim(user), contentType: ScimContentType,
            statusCode: result.Created ? StatusCodes.Status201Created : StatusCodes.Status200OK);
    }

    /// <summary>
    /// Entra's deprovisioning is a PATCH that sets <c>active</c> to false; the same shape brings it
    /// back when somebody returns. Attribute edits arrive as a full PUT, handled below.
    /// </summary>
    private static async Task<IResult> PatchUserAsync(
        string id,
        JsonElement payload,
        IExternalDirectoryService directory,
        D3ParkingDbContext dbContext,
        CancellationToken cancellationToken)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            return ScimError(StatusCodes.Status400BadRequest, "Expected a patch object.");
        }
        await using var transaction = await dbContext.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var user = await FindAsync(dbContext, id, cancellationToken);
        if (user is null)
        {
            return ScimError(StatusCodes.Status404NotFound, "User not found.");
        }

        if (IsOtherDirectory(user))
        {
            return ScimError(StatusCodes.Status409Conflict, "The target account belongs to another directory.");
        }

        if (!TryReadActiveFromPatch(payload, out var active))
        {
            return ScimError(StatusCodes.Status400BadRequest, "Expected patch operations with boolean active values.");
        }
        if (active is null)
        {
            // Nothing this endpoint acts on; report the account unchanged rather than fail the cycle.
            return Results.Json(ToScim(user), contentType: ScimContentType);
        }

        if (user.ExternalProvider != ExternalProviders.EntraId || string.IsNullOrWhiteSpace(user.ExternalObjectId))
        {
            return ScimError(StatusCodes.Status409Conflict, "The target account is not linked to this directory.");
        }
        var result = await directory.SetActiveAsync(
            user.ExternalProvider ?? ExternalProviders.EntraId,
            user.ExternalObjectId ?? string.Empty,
            active.Value,
            cancellationToken);

        if (!result.Succeeded)
        {
            // Report the refused transition as a conflict, for example when it would remove the
            // last administrator. The client's retry policy is outside this endpoint's control.
            return ScimError(StatusCodes.Status409Conflict, string.Join(" ", result.Errors));
        }

        var refreshed = await FindAsync(dbContext, id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Results.Json(ToScim(refreshed!), contentType: ScimContentType);
    }

    private static async Task<IResult> ReplaceUserAsync(
        string id,
        JsonElement payload,
        IExternalDirectoryService directory,
        D3ParkingDbContext dbContext,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var user = await FindAsync(dbContext, id, cancellationToken);
        if (user is null)
        {
            return ScimError(StatusCodes.Status404NotFound, "User not found.");
        }

        if (IsOtherDirectory(user))
        {
            return ScimError(StatusCodes.Status409Conflict, "The target account belongs to another directory.");
        }
        if (!TryReadActive(payload, out var active))
        {
            return ScimError(StatusCodes.Status400BadRequest, "Expected a user object with a boolean active attribute when provided.");
        }
        var parsed = ReadIdentity(payload, active);
        if (parsed is null)
        {
            return ScimError(StatusCodes.Status400BadRequest, "externalId and userName are required.");
        }

        // The object id on the wire is advisory; the one already stored is what this account is.
        var identity = parsed with { ObjectId = user.ExternalObjectId ?? parsed.ObjectId };
        var result = await directory.SyncAsync(identity, cancellationToken);
        if (!result.Succeeded)
        {
            return ScimError(StatusCodes.Status400BadRequest, string.Join(" ", result.Errors));
        }

        // Při adopci místního účtu může tělo ukazovat na jinou existující identitu.
        // Kontrola musí být před commitem; samotné odmítnutí po SyncAsync by nechalo cizí změny.
        if (result.UserId != user.Id)
        {
            return ScimError(StatusCodes.Status409Conflict, "The supplied identity does not match the target account.");
        }

        if (active is { } requestedActive)
        {
            var changed = await directory.SetActiveAsync(identity.Provider, identity.ObjectId, requestedActive, cancellationToken);
            if (!changed.Succeeded)
            {
                return ScimError(StatusCodes.Status409Conflict, string.Join(" ", changed.Errors));
            }
        }

        var refreshed = await FindAsync(dbContext, id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Results.Json(ToScim(refreshed!), contentType: ScimContentType);
    }

    /// <summary>
    /// A delete blocks the account instead of removing it. Reservations, points and the audit trail
    /// are records of things that happened, and an offboarding in the directory is not a reason to
    /// rewrite history — nor is it rare for the same person to come back.
    /// </summary>
    private static async Task<IResult> DeleteUserAsync(
        string id,
        IExternalDirectoryService directory,
        D3ParkingDbContext dbContext,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var user = await FindAsync(dbContext, id, cancellationToken);
        if (user is null)
        {
            return ScimError(StatusCodes.Status404NotFound, "User not found.");
        }

        if (IsOtherDirectory(user))
        {
            return ScimError(StatusCodes.Status409Conflict, "The target account belongs to another directory.");
        }

        if (user.ExternalProvider != ExternalProviders.EntraId || string.IsNullOrWhiteSpace(user.ExternalObjectId))
        {
            return ScimError(StatusCodes.Status409Conflict, "The target account is not linked to this directory.");
        }
        var result = await directory.SetActiveAsync(
            user.ExternalProvider ?? ExternalProviders.EntraId,
            user.ExternalObjectId ?? string.Empty,
            active: false,
            cancellationToken);

        if (!result.Succeeded)
        {
            return ScimError(StatusCodes.Status409Conflict, string.Join(" ", result.Errors));
        }
        await transaction.CommitAsync(cancellationToken);
        return Results.NoContent();
    }

    /// <summary>Accepts either this application's own id or the directory's object id.</summary>
    private static async Task<ApplicationUser?> FindAsync(D3ParkingDbContext dbContext, string id, CancellationToken cancellationToken)
    {
        // ID vydané aplikací má přednost před shodným textem oid jiné identity.
        if (Guid.TryParse(id, out var localId))
        {
            var local = await dbContext.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == localId, cancellationToken);
            if (local is not null) return local;
        }
        return await dbContext.Users.AsNoTracking().FirstOrDefaultAsync(
            u => u.ExternalProvider == ExternalProviders.EntraId && u.ExternalObjectId == id, cancellationToken);
    }

    private static bool IsOtherDirectory(ApplicationUser user) =>
        user.ExternalProvider is not null && user.ExternalProvider != ExternalProviders.EntraId;

    private static bool TryReadActive(JsonElement payload, out bool? active)
    {
        active = null;
        if (payload.ValueKind != JsonValueKind.Object) return false;
        if (!payload.TryGetProperty("active", out var value)) return true;
        active = ReadBoolean(value);
        return active.HasValue;
    }

    private static ExternalIdentity? ReadIdentity(JsonElement payload, bool? active)
    {
        var externalId = GetString(payload, "externalId");
        var userName = GetString(payload, "userName");
        if (string.IsNullOrWhiteSpace(externalId) || string.IsNullOrWhiteSpace(userName))
        {
            return null;
        }

        var email = ReadPrimaryEmail(payload) ?? userName;
        var displayName = GetString(payload, "displayName");
        if (string.IsNullOrWhiteSpace(displayName)
            && payload.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.Object)
        {
            displayName = string.Join(' ', new[] { GetString(name, "givenName"), GetString(name, "familyName") }
                .Where(part => !string.IsNullOrWhiteSpace(part)));
        }

        return new ExternalIdentity(
            ExternalProviders.EntraId,
            externalId!,
            email,
            TenantId: null,
            DisplayName: string.IsNullOrWhiteSpace(displayName) ? null : displayName,
            Department: GetString(payload, "department")
                ?? ReadEnterpriseAttribute(payload, "department"),
            // Provisioning says nothing about roles; app role claims at sign-in do. Null keeps this
            // push from being read as "revoke everything".
            Roles: null,
            Active: active ?? true);
    }

    private static string? ReadPrimaryEmail(JsonElement payload)
    {
        if (!payload.TryGetProperty("emails", out var emails) || emails.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        string? first = null;
        foreach (var entry in emails.EnumerateArray())
        {
            var value = GetString(entry, "value");
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            first ??= value;
            if (entry.TryGetProperty("primary", out var primary) && primary.ValueKind == JsonValueKind.True)
            {
                return value;
            }
        }

        return first;
    }

    private static string? ReadEnterpriseAttribute(JsonElement payload, string attribute)
    {
        const string enterprise = "urn:ietf:params:scim:schemas:extension:enterprise:2.0:User";
        return payload.TryGetProperty(enterprise, out var extension) && extension.ValueKind == JsonValueKind.Object
            ? GetString(extension, attribute)
            : null;
    }

    /// <summary>
    /// Reads the active flag out of a SCIM PATCH. Entra sends <c>{"op":"Replace","path":"active"}</c>
    /// but the operation name's case and the boolean's type (real or stringified) both vary between
    /// its provisioning versions, so all the shapes are accepted.
    /// </summary>
    private static bool TryReadActiveFromPatch(JsonElement payload, out bool? active)
    {
        active = null;
        if (!payload.TryGetProperty("Operations", out var operations)
            && !payload.TryGetProperty("operations", out operations))
        {
            return true;
        }

        if (operations.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var operation in operations.EnumerateArray())
        {
            if (operation.ValueKind != JsonValueKind.Object) return false;
            var op = GetString(operation, "op") ?? GetString(operation, "Op");
            if (op is not null && !op.Equals("replace", StringComparison.OrdinalIgnoreCase)
                               && !op.Equals("add", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var path = GetString(operation, "path") ?? GetString(operation, "Path");
            if (!operation.TryGetProperty("value", out var value)
                && !operation.TryGetProperty("Value", out value))
            {
                continue;
            }

            if (string.Equals(path, "active", StringComparison.OrdinalIgnoreCase))
            {
                if (ReadBoolean(value) is not { } parsed) return false;
                active = parsed;
            }
            else if (path is null && value.ValueKind == JsonValueKind.Object
                     && value.TryGetProperty("active", out var nested))
            {
                if (ReadBoolean(nested) is not { } parsed) return false;
                active = parsed;
            }
        }

        return true;
    }

    private static bool? ReadBoolean(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String => bool.TryParse(value.GetString(), out var parsed) ? parsed : null,
        _ => null,
    };

    private static string? ParseUserNameFilter(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return null;
        }

        // userName eq "someone@example.com"
        var parts = filter.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3
            || !parts[0].Equals("userName", StringComparison.OrdinalIgnoreCase)
            || !parts[1].Equals("eq", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return parts[2].Trim('"');
    }

    private static object ToScim(ApplicationUser user) => new
    {
        schemas = new[] { UserSchema },
        id = user.Id.ToString(),
        externalId = user.ExternalObjectId,
        userName = user.UserName,
        displayName = user.DisplayName,
        active = user.Status == AccountStatus.Active,
        emails = new[] { new { value = user.Email, type = "work", primary = true } },
        meta = new { resourceType = "User" },
    };

    private static IResult ScimList(IReadOnlyList<object> resources) => Results.Json(new
    {
        schemas = new[] { ListSchema },
        totalResults = resources.Count,
        startIndex = 1,
        itemsPerPage = resources.Count,
        Resources = resources,
    }, contentType: ScimContentType);

    private static IResult ScimError(int status, string detail) => Results.Json(new
    {
        schemas = new[] { ErrorSchema },
        status = status.ToString(),
        detail,
    }, contentType: ScimContentType, statusCode: status);

    private static string? GetString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool FixedTimeEquals(string presented, string expected) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(expected));
}
