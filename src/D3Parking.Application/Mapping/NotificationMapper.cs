using Riok.Mapperly.Abstractions;
using D3Parking.Contracts.Notifications;
using D3Parking.Domain.Notifications;

namespace D3Parking.Application.Mapping;

[Mapper]
public partial class NotificationMapper
{
    [MapperIgnoreSource(nameof(Notification.UserId))]
    [MapperIgnoreSource(nameof(Notification.ReadAtUtc))]
    public partial NotificationDto ToDto(Notification notification);

    public partial IQueryable<NotificationDto> ProjectToDtos(IQueryable<Notification> source);
}
