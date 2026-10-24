using Jibbers.Sdk;

namespace JibbersAlpineVision.Rendering;

/// <summary>Builds a bounded shadow-only heightfield tile. It never changes visible terrain.</summary>
internal sealed record TerrainShadowMesh(Vector3[] Vertices, Vector3[] Normals, int[] Triangles)
{
    public static TerrainShadowMesh Build(float[] heights,int sourceWidth,int sourceDepth,
        float width,float depth,float heightRange,float spacing)
    {
        if(sourceWidth<2 || sourceDepth<2 || heights.Length!=checked(sourceWidth*sourceDepth))
            throw new ArgumentException("Invalid terrain heightfield dimensions");
        if(!float.IsFinite(width+depth+heightRange+spacing) || width<=0 || depth<=0 || heightRange<=0 || spacing<=0)
            throw new ArgumentException("Invalid terrain shadow dimensions");
        if(Array.Exists(heights,height=>!float.IsFinite(height) || height<0 || height>1))
            throw new ArgumentException("Invalid normalized terrain height");
        var columns=Math.Clamp((int)MathF.Ceiling(width/spacing),1,254);
        var rows=Math.Clamp((int)MathF.Ceiling(depth/spacing),1,254);
        var vertices=new Vector3[(columns+1)*(rows+1)];
        var normals=new Vector3[vertices.Length];
        var triangles=new int[columns*rows*6];
        int SourceX(int column)=>(int)Math.Round((double)column*(sourceWidth-1)/columns);
        int SourceZ(int row)=>(int)Math.Round((double)row*(sourceDepth-1)/rows);
        float Height(int x,int z)=>heights[z*sourceWidth+x]*heightRange;
        for(var row=0;row<=rows;row++)
        for(var column=0;column<=columns;column++)
        {
            var x=SourceX(column);var z=SourceZ(row);
            var vertex=row*(columns+1)+column;
            vertices[vertex]=new((float)x/(sourceWidth-1)*width,Height(x,z),(float)z/(sourceDepth-1)*depth);
            var left=Math.Max(0,x-1);var right=Math.Min(sourceWidth-1,x+1);
            var back=Math.Max(0,z-1);var front=Math.Min(sourceDepth-1,z+1);
            var slopeX=(Height(right,z)-Height(left,z))/((float)(right-left)/(sourceWidth-1)*width);
            var slopeZ=(Height(x,front)-Height(x,back))/((float)(front-back)/(sourceDepth-1)*depth);
            var normal=new Vector3(-slopeX,1,-slopeZ);
            normals[vertex]=normal*(1/normal.Magnitude);
        }
        var triangle=0;
        for(var row=0;row<rows;row++)
        for(var column=0;column<columns;column++)
        {
            var start=row*(columns+1)+column;
            triangles[triangle++]=start;triangles[triangle++]=start+columns+1;triangles[triangle++]=start+1;
            triangles[triangle++]=start+1;triangles[triangle++]=start+columns+1;triangles[triangle++]=start+columns+2;
        }
        return new(vertices,normals,triangles);
    }
}
