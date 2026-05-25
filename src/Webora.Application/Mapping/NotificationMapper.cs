using Riok.Mapperly.Abstractions;
using Webora.Application.Notifications;
using Webora.Domain.Notifications;

namespace Webora.Application.Mapping;

[Mapper]
public partial class NotificationMapper
{
    [MapperIgnoreSource(nameof(Notification.UserId))]
    [MapperIgnoreSource(nameof(Notification.ReadAtUtc))]
    public partial NotificationDto ToDto(Notification notification);

    public partial IQueryable<NotificationDto> ProjectToDtos(IQueryable<Notification> source);
}
