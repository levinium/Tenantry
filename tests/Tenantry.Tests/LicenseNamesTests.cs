using Tenantry.Core.Licensing;

namespace Tenantry.Tests;

public class LicenseNamesTests
{
    [Fact]
    public void The_embedded_table_loads()
    {
        Assert.True(LicenseNames.Count > 200);
    }

    [Theory]
    [InlineData("cbdc14ab-d96c-4c30-b9f4-6ada7cdc1d46", "SPB", "Microsoft 365 Business Premium")]
    [InlineData("CBDC14AB-D96C-4C30-B9F4-6ADA7CDC1D46", null, "Microsoft 365 Business Premium")]
    [InlineData("00000000-0000-0000-0000-000000000000", "FLOW_FREE", "Microsoft Power Automate Free")]
    [InlineData("00000000-0000-0000-0000-000000000000", "NOT_A_REAL_SKU", "NOT_A_REAL_SKU")]
    [InlineData("11111111-1111-1111-1111-111111111111", null, "11111111-1111-1111-1111-111111111111")]
    public void Names_by_id_then_code_then_raw(string skuId, string? code, string expected) =>
        Assert.Equal(expected, LicenseNames.For(skuId, code));
}
