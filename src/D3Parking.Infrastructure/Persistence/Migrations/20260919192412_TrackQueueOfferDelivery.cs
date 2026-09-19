using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace D3Parking.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TrackQueueOfferDelivery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "OfferEmailDeliveryId",
                table: "QueueEntries",
                type: "uniqueidentifier",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OfferEmailDeliveryId",
                table: "QueueEntries");
        }
    }
}
