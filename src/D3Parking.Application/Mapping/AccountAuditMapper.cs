using Riok.Mapperly.Abstractions;
using D3Parking.Application.Accounts;
using D3Parking.Domain.Accounts;

namespace D3Parking.Application.Mapping;

[Mapper]
public partial class AccountAuditMapper
{
    public partial AccountAuditEntry ToEntry(AccountAuditEvent auditEvent);

    public partial IQueryable<AccountAuditEntry> ProjectToEntries(IQueryable<AccountAuditEvent> source);
}
