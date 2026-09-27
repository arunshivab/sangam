using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Persistence;

namespace Sangam.Identity.Infrastructure.Seeding;

/// <summary>
/// Creates the very first platform operator from the command line, because the console cannot
/// grant access to anyone until someone holds it. Deliberately one-shot: it refuses as soon as
/// any operator exists, so it can never be used to quietly add a second one later.
/// </summary>
public static class OperatorBootstrapper
{
    /// <summary>The verb: <c>dotnet run -- create-operator someone@example.in</c>.</summary>
    public const string CommandName = "create-operator";

    /// <summary>Runs the command.</summary>
    /// <param name="services">Host services.</param>
    /// <param name="args">Command-line arguments, the first being the verb.</param>
    /// <returns>A process exit code: 0 on success.</returns>
    public static async Task<int> RunAsync(IServiceProvider services, string[] args)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(args);

        if (args.Length < 2 || string.IsNullOrWhiteSpace(args[1]))
        {
            Console.Error.WriteLine($"Usage: dotnet run -- {CommandName} <email>");
            return 2;
        }

        using IServiceScope scope = services.CreateScope();
        SangamDbContext db = scope.ServiceProvider.GetRequiredService<SangamDbContext>();
        IClock clock = scope.ServiceProvider.GetRequiredService<IClock>();

        if (await db.PlatformOperators.AnyAsync(o => o.RevokedAt == null).ConfigureAwait(false))
        {
            Console.Error.WriteLine("Sangam already has at least one operator. Grant access from the console instead.");
            return 1;
        }

        string normalised = args[1].Trim().ToUpperInvariant();
        SangamUser? user = await db.Users.FirstOrDefaultAsync(u => u.NormalizedEmail == normalised).ConfigureAwait(false);
        if (user is null)
        {
            Console.Error.WriteLine($"No Sangam account has the email {args[1]}. Register it first, verify it, then run this again.");
            return 1;
        }

        if (user.Status != UserStatus.Active || !user.EmailConfirmed)
        {
            Console.Error.WriteLine("That account is not active and verified.");
            return 1;
        }

        db.PlatformOperators.Add(new PlatformOperator
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            Role = PlatformRole.Owner,
            GrantedByUserId = null,
            GrantedAt = clock.UtcNow,
        });

        db.AuditEvents.Add(new AuditEvent
        {
            Action = AuditActions.AdminOperatorGrant,
            ActorType = AuditActorType.System,
            TargetType = "user",
            TargetId = user.Id,
            Metadata = "{\"role\":\"owner\",\"via\":\"bootstrap\"}",
            OccurredAt = clock.UtcNow,
        });

        await db.SaveChangesAsync().ConfigureAwait(false);
        Console.WriteLine($"{user.Email} is now the owner of the Sangam console.");
        Console.WriteLine("They must set up an authenticator app in the account portal before the console will let them in.");
        return 0;
    }
}
