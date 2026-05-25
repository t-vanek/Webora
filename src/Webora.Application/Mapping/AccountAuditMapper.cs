using Riok.Mapperly.Abstractions;
using Webora.Application.Accounts;
using Webora.Domain.Accounts;

namespace Webora.Application.Mapping;

[Mapper]
public partial class AccountAuditMapper
{
    public partial AccountAuditEntry ToEntry(AccountAuditEvent auditEvent);

    public partial IQueryable<AccountAuditEntry> ProjectToEntries(IQueryable<AccountAuditEvent> source);
}
