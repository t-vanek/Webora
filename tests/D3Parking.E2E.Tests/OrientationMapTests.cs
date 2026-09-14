using Microsoft.Playwright;
using NUnit.Framework;

namespace D3Parking.E2E.Tests;

[TestFixture]
public class OrientationMapTests : AdminTest
{
    [Test]
    public async Task An_orientation_image_upload_is_served_as_a_raster_image()
    {
        await Pages.GotoInteractiveAsync(Page, "/admin/parking/settings");
        await Page.Locator("details").Filter(new() { Has = Page.Locator("#parking-orientation-map") }).Locator("summary").ClickAsync();
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=");
        await Page.Locator("#parking-orientation-map").SetInputFilesAsync(new FilePayload { Name = "orientation.png", MimeType = "image/png", Buffer = png });
        await Expect(Page.Locator(".orientation-map-admin__preview img")).ToBeVisibleAsync();
        var response = await Page.APIRequest.GetAsync(WebAppFixture.BaseUrl + "/api/parking/orientation-map");
        Assert.That(response.Status, Is.EqualTo(200));
        Assert.That(response.Headers["content-type"], Is.EqualTo("image/png"));
        Assert.That(response.Headers["x-content-type-options"], Is.EqualTo("nosniff"));
    }
}
