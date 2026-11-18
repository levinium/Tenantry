using Tenantry.Core.Changes;
using Tenantry.Graph;

namespace Tenantry.Tests;

/// <summary>Deleting a connector: the right cmdlet for its direction, the name typed to confirm, Test mode honoured.</summary>
public sealed class ConnectorTests
{
    static ChangeTarget Named(string name) => new("connector", name, name, null);

    [Fact]
    public void Delete_uses_the_cmdlet_for_its_direction()
    {
        var scripts = new GraphChangeScripts();
        Assert.Contains("Remove-InboundConnector", scripts.For(new RemoveConnector(Named("From the filter"), Inbound: true))!.Text);
        Assert.Contains("Remove-OutboundConnector", scripts.For(new RemoveConnector(Named("To the partner"), Inbound: false))!.Text);
    }

    [Fact]
    public void Delete_is_a_caution_change_confirmed_by_typing_the_name()
    {
        var change = new RemoveConnector(Named("To the partner"), Inbound: false);
        Assert.True(change.IsCaution);
        Assert.Equal("To the partner", change.ConfirmPhrase);
    }

    [Fact]
    public void Test_mode_refuses_a_real_connector_and_allows_a_test_one()
    {
        Assert.NotNull(WriteGuard.Refusal(new RemoveConnector(Named("To the partner"), false), testMode: true));
        Assert.Null(WriteGuard.Refusal(new RemoveConnector(Named("zz-tenantry-test-connector"), false), testMode: true));
    }
}
