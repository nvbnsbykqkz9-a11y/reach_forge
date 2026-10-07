using ReachForge.Application.Services;
using ReachForge.Domain.Enums;

namespace ReachForge.Application.Tests;

public class ChannelServiceMessageTests
{
    [Theory]
    [InlineData(SocialPlatform.X, "Social:X:ClientId")]
    [InlineData(SocialPlatform.Instagram, "Social:Meta:AppId")]
    [InlineData(SocialPlatform.TikTok, "Social:TikTok:ClientKey")]
    [InlineData(SocialPlatform.YouTube, "Social:YouTube:ClientId")]
    public void Missing_app_settings_message_names_the_keys_to_set(SocialPlatform platform, string key)
    {
        var message = ChannelService.NotConfiguredMessage(platform);
        Assert.Contains(key, message);
        Assert.Contains("再起動", message);
        Assert.DoesNotContain("準備中", message);
    }
}
