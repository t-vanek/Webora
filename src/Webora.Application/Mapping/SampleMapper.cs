using Riok.Mapperly.Abstractions;

namespace Webora.Application.Mapping;

// Reference example of the Mapperly pattern used across the application layer.
// Mapperly generates the mapping code at compile time (no runtime reflection).
// Replace SampleEntity/SampleDto with real domain types as features are added.

public class SampleEntity
{
    public Guid Id { get; init; }
    public string Title { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; }
}

public record SampleDto(Guid Id, string Title, DateTimeOffset CreatedAt);

[Mapper]
public partial class SampleMapper
{
    public partial SampleDto ToDto(SampleEntity entity);

    // IQueryable projection — the replacement for AutoMapper's ProjectTo. EF Core
    // translates this to SQL so only the projected columns are fetched.
    public partial IQueryable<SampleDto> ProjectToDto(IQueryable<SampleEntity> source);
}
