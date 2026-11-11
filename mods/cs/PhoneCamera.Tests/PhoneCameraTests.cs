using System.Runtime.CompilerServices;
using Jibbers.Sdk;
using PhoneCamera;
using PhoneCamera.Integration;

static class PhoneCameraTests
{
    static bool Near(float a, float b, float within = 1e-3f) => MathF.Abs(a - b) < within;

    [Test]
    static void TheModDescribesItself()
    {
        var mod = (PhoneCameraMod)RuntimeHelpers.GetUninitializedObject(typeof(PhoneCameraMod));
        Assert.Equal("phone_camera", mod.Id);
        Assert.Equal("Phone Camera", mod.Name);
    }

    [Test]
    static void LookingRotatesForwardOntoTheDirection()
    {
        foreach (var d in new Vector3[] { new(0, 0, 1), new(1, 0, 0), new(-1, 0.5f, -2), new(0.2f, -0.9f, 0.1f) })
        {
            var f = CamMath.Rotate(CamMath.LookRotation(d), new(0, 0, 1));
            var n = CamMath.Normalize(d);
            Assert.True(Near(f.X, n.X) && Near(f.Y, n.Y) && Near(f.Z, n.Z), $"{d} -> {f}");
        }
    }

    [Test]
    static void TurningGoesRightAndUpAndStopsShortOfStraightUp()
    {
        var right = CamMath.Turn(new(0, 0, 1), 90, 0);
        Assert.True(Near(right.X, 1) && Near(right.Z, 0));
        var up = CamMath.Turn(new(0, 0, 1), 0, 89);
        Assert.True(up.Y < 0.995f && up.Y > 0.99f);
    }

    [Test]
    static void ZoomGoesFromATenthToEight()
    {
        var z = 1f;
        for (var i = 0; i < 30; i++) z = CamMath.Zoom(z, -1);
        Assert.Equal(0.1f, z);
        for (var i = 0; i < 40; i++) z = CamMath.Zoom(z, 1);
        Assert.Equal(8f, z);
    }

    [Test]
    static void ZoomingOutWidensLikeALensWithoutPassing180()
    {
        Assert.True(Near(CamMath.FieldOfView(false, null, 1, false), 60, 0.01f));
        var wide = CamMath.FieldOfView(false, null, 0.1f, false);
        Assert.True(wide > 150 && wide <= 170, $"{wide}");
        Assert.True(CamMath.FieldOfView(false, null, 8, false) < 10);
        Assert.True(CamMath.FieldOfView(false, null, 1, true) > 60); // upright is widened
    }

    [Test]
    static void FollowingKeepsAboutThreeAndAHalfMetresInFrame()
    {
        var fov = CamMath.FieldOfView(false, 20, 1, false);
        var across = 2 * 20 * MathF.Tan(fov * MathF.PI / 360);
        Assert.True(Near(across, 3.5f, 0.01f), $"{across}");
    }

    [Test]
    static void QualityPicksTheScreenSize()
    {
        Assert.Equal((960, 540), CamMath.FeedSize("540p", false));
        Assert.Equal((1280, 720), CamMath.FeedSize("720p", false));
        Assert.Equal((1080, 1920), CamMath.FeedSize("1080p", true));
        Assert.Equal((2560, 1440), CamMath.FeedSize("1440p", false));
        Assert.True(CamMath.Bitrate(2560, 1440, 30) > CamMath.Bitrate(960, 540, 30));
    }

    [Test]
    static void SavedPosesAreFoundByNameIgnoringCaseAndSpaces()
    {
        var json = "{\"id\":\"x\",\"name\":\"HoldPhonecamera\",\"data-count\":3,\"data\":{\"value-0\":0.202857152,\"value-1\":1.0,\"value-2\":0.0}}";
        var v = PoseFile.Parse(json, "hold phone camera");
        Assert.True(v is { Length: 3 } && Near(v[0], 0.2028f) && v[1] == 1 && v[2] == 0);
        Assert.True(PoseFile.Parse(json, "Casual") is null);
    }

    [Test]
    static void ThePhoneHasAHoleForThePictureAndARedRecordButton()
    {
        foreach (var portrait in new[] { false, true })
        {
            var l = PhoneLayout.For(portrait);
            var px = PhoneArt.Draw(l, recording: false);
            int At(float x, float y) => ((int)y * l.W + (int)x) * 4;
            var middle = At(l.ScreenX + l.ScreenW / 2, l.ScreenY + l.ScreenH / 2);
            Assert.Equal((byte)0, px[middle + 3]); // see-through
            var bezel = At(l.ScreenX / 2, l.ScreenY + l.ScreenH / 2);
            Assert.Equal((byte)255, px[bezel + 3]);
            var dot = At(l.ButtonX, l.ButtonY);
            Assert.True(px[dot] > 200 && px[dot + 1] < 100, "red dot");
        }
    }

    [Test]
    static void ClipsEncodeToMp4()
    {
        if (!OperatingSystem.IsWindows()) return;
        foreach (var (w, h) in new[] { (960, 540), (540, 960) })
        {
            var path = Path.Combine(Path.GetTempPath(), $"phone_camera_test_{w}x{h}.mp4");
            File.Delete(path);
            Assert.Equal(30L, ClipRecorder.EncodeForTest(path, w, h, 30));
            Assert.True(new FileInfo(path).Length > 1000);
            File.Delete(path);
        }
    }
}
