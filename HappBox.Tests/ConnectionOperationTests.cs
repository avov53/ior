using HappBox;
using Xunit;

namespace HappBox.Tests;

public sealed class ConnectionOperationTests
{
    [Fact]
    public void CompletionPersistsForLateSnapshotAndCannotBeOverwritten()
    {
        var operation = new ConnectionOperation("connection.toggle");
        operation.Advance("probe");
        Assert.Equal("probe", operation.Stage);
        operation.Complete(true);
        operation.Fail("late failure");
        operation.Advance("late stage");
        Assert.Equal("succeeded", operation.Status);
        Assert.Equal("complete", operation.Stage);
        Assert.Equal(true, operation.Result);
        Assert.Null(operation.Error);
    }

    [Fact]
    public void FailedOperationKeepsPrimaryErrorAndUniqueIdentity()
    {
        var operation = new ConnectionOperation("connection.mode");
        operation.Fail("NETWORK_ROLLBACK_PENDING");
        operation.Complete(null);
        Assert.Equal("failed", operation.Status);
        Assert.Equal("NETWORK_ROLLBACK_PENDING", operation.Error);
        Assert.NotEqual(operation.Id, new ConnectionOperation("connection.mode").Id);
    }
}
