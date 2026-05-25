using System.Numerics;

namespace DesktopPet.Rendering.D3D11.Tests;

[TestClass]
public sealed class D3D11MeshItemTests
{
    [TestMethod]
    public void ConstructorStoresMeshPayload()
    {
        var vertices = new[]
        {
            new D3D11MeshVertex(0, 0, 0, 1, 0, 0, 1),
            new D3D11MeshVertex(1, 0, 0, 0, 1, 0, 1),
            new D3D11MeshVertex(0, 1, 0, 0, 0, 1, 1)
        };
        var indices = new ushort[] { 0, 1, 2 };
        var world = Matrix4x4.CreateScale(1.25f) * Matrix4x4.CreateRotationY(0.5f);

        var item = new D3D11MeshItem("Triangle", vertices, indices, world);

        Assert.AreEqual("Triangle", item.Id);
        Assert.HasCount(3, item.Vertices);
        Assert.HasCount(3, item.Indices);
        Assert.AreEqual(world, item.WorldTransform);
    }
}
