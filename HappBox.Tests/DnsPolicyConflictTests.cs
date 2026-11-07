using HappBox;
using Xunit;
namespace HappBox.Tests;
public sealed class DnsPolicyConflictTests
{
    [Fact] public void IdentifiesConfirmedAgWithoutLeakingMetadata()
    {
        var text=DnsPolicyConflict.Message("""{"Rules":[{"Namespace":["private.example"],"Comment":"AG_UNLOCKER_NRPT_V2","DisplayName":"AG Unlocker secret-id"}]}""");
        Assert.Contains("AG Unlocker",text);Assert.Contains("Простое завершение",text);
        Assert.DoesNotContain("secret-id",text);Assert.DoesNotContain("private.example",text);
    }
    [Theory]
    [InlineData("{}")]
    [InlineData("invalid")]
    [InlineData("{\"Rules\":[{\"Namespace\":\".\",\"DisplayName\":\"AG Unlocker unconfirmed\"}]}")]
    public void UnknownOwnerIsNotGuessed(string report)=>Assert.DoesNotContain("AG Unlocker",DnsPolicyConflict.Message(report));
    [Fact] public void CorporateMetadataRecommendsAdministrator()
    {
        Assert.Contains("администратору",DnsPolicyConflict.Message("""{"Effective":[{"Namespace":".","DirectAccessEnabled":true}]}"""));
    }
    [Fact] public void EmptyRuleDoesNotIdentifyOwner()=>Assert.DoesNotContain("AG Unlocker",DnsPolicyConflict.Message("""{"Rules":[{"Namespace":[],"Comment":"AG_UNLOCKER_NRPT_V2","DisplayName":"AG Unlocker id"}]}"""));
}
