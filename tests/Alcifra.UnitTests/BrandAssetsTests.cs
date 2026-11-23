using System.Text;
using Alcifra.Theming;

namespace Alcifra.UnitTests;

// A pack's asset is addressed by its KEY, so the format comes from the served type or the bytes, never from a
// file name that does not exist (the desktop drew a PNG decoder over the Bund's SVG, 2026-11-23).
public sealed class BrandAssetsTests
{
    private static readonly byte[] Svg = Encoding.UTF8.GetBytes(
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 62 70'><rect width='62' height='70' fill='#f00'/></svg>");

    [Fact]
    public void The_served_type_decides()
    {
        Assert.True(BrandAssets.IsSvg(Svg, "image/svg+xml"));
        Assert.False(BrandAssets.IsSvg([0x89, 0x50, 0x4E, 0x47], "image/png"));
    }

    [Fact]
    public void Without_a_worthwhile_type_the_bytes_decide()
    {
        Assert.True(BrandAssets.IsSvg(Svg, null));
        Assert.True(BrandAssets.IsSvg(Svg, "application/octet-stream"));
        Assert.False(BrandAssets.IsSvg([0x89, 0x50, 0x4E, 0x47], "application/octet-stream"));
    }

    [Theory]
    [InlineData("<?xml version='1.0'?><svg></svg>")]
    [InlineData("﻿  \n<svg viewBox='0 0 1 1'></svg>")]
    [InlineData("<!-- a mark --><svg></svg>")]
    public void An_xml_document_is_an_svg_however_it_opens(string document)
    {
        Assert.True(BrandAssets.IsSvg(Encoding.UTF8.GetBytes(document), "application/octet-stream"));
    }
}
