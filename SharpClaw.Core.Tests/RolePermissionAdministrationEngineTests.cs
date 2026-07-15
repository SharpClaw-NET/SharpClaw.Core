using SharpClaw.Core.Permissions;
using SharpClaw.Core.State;

namespace SharpClaw.Core.Tests;

public sealed class RolePermissionAdministrationEngineTests
{
    [Fact]
    public void PlanDeleteRole_ClearsUserForeignKeyAndReference()
    {
        var role = new RoleState { Name = "operators" };
        var user = new UserState
        {
            Username = "operator",
            PasswordHash = [1],
            PasswordSalt = [2],
            RoleId = Guid.NewGuid(),
            Role = role
        };
        role.Users.Add(user);

        _ = new RolePermissionAdministrationEngine().PlanDeleteRole(role);

        Assert.Null(user.RoleId);
        Assert.Null(user.Role);
    }
}
