using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ConvertOBJToBedrockModel
{
    class Program
    {
        static void Main(string[] args)
        {
	        var lines = File.ReadAllLines(args[0]);
            var name = new FileInfo(args[0]).Name;
            var (vertices, normals, uvs, groups) = ExtractOBJData(lines);

            var root = CreateEntityModel(name, groups, vertices, normals, uvs);

            //Strictly controlled JSON formatting.
            var stringWriter = new StringWriter();
            var writer = new JsonTextWriter(stringWriter) {Formatting = Formatting.Indented};
            SerializeJson(writer, root);
            
            var result = stringWriter.ToString();
            Console.WriteLine(result);
        }

        private static JObject CreateEntityModel(string name, IEnumerable<Group> groups, IReadOnlyList<Vertex> vertices, IReadOnlyList<VertexNormal> normals, IReadOnlyList<TextureUv> uvs)
        {
            var bones = new JArray();
            foreach (var g in groups)
            {
                var positionMap = new Dictionary<int, int>();
                var normalMap = new Dictionary<int, int>();
                var uvMap = new Dictionary<int, int>();

                var jsPolys = new JArray();
                var jsPositions = new JArray();
                var jsNormals = new JArray();
                var jsUvs = new JArray();

                foreach (var polygonData in g.Polygons)
                {
                    var jsPoly = new JArray();
                    jsPolys.Add(jsPoly);

                    foreach (var elementData in polygonData.Elements)
                    {
                        var vertexIndex = elementData.VertexIndex - 1;
                        var normalIndex = elementData.NormalIndex - 1;
                        var uvIndex = elementData.TextureUvIndex - 1;

                        if (!positionMap.TryGetValue(vertexIndex, out var localVertexIndex))
                        {
                            positionMap.Add(vertexIndex, localVertexIndex = jsPositions.Count);
                            jsPositions.Add(new JArray
                            {
                                new JValue(vertices[vertexIndex].X),
                                new JValue(vertices[vertexIndex].Y),
                                new JValue(vertices[vertexIndex].Z)
                            });
                        }

                        if (!normalMap.TryGetValue(normalIndex, out var localNormalIndex))
                        {
                            normalMap.Add(normalIndex, localNormalIndex = jsNormals.Count);
                            jsNormals.Add(new JArray
                            {
                                new JValue(normals[normalIndex].X),
                                new JValue(normals[normalIndex].Y),
                                new JValue(normals[normalIndex].Z)
                            });
                        }

                        if (!uvMap.TryGetValue(uvIndex, out var localUvIndex))
                        {
                            uvMap.Add(uvIndex, localUvIndex = jsUvs.Count);
                            jsUvs.Add(new JArray
                            {
                                new JValue(uvs[uvIndex].U),
                                new JValue(uvs[uvIndex].V)
                            });
                        }

                        jsPoly.Add(new JArray
                        {
                            new JValue(localVertexIndex),
                            new JValue(localNormalIndex),
                            new JValue(localUvIndex)
                        });
                    }
                }

                bones.Add(new JObject
                {
                    {"name", g.Name},
                    {
                        "pivot", new JArray
                        {
                            new JValue(0),
                            new JValue(0),
                            new JValue(0)
                        }
                    },
                    {
                        "poly_mesh", new JObject
                        {
                            {"positions", jsPositions},
                            {"normals", jsNormals},
                            {"uvs", jsUvs},
                            {"polys", jsPolys}
                        }
                    }
                });
            }

            var geometry = new JObject
            {
                {"texturewidth", 16}, //TODO: Resolve this
                {"textureheight", 16},
                {"visible_bounds_width", 1}, //TODO: Figure this out.
                {"visible_bounds_height", 1},
                {"bones", bones}
            };

            var root = new JObject
            {
                {"format_version", new JValue("1.8.0")},
                {$"geometry.{name}", geometry}
            };
            return root;
        }

        private static (List<Vertex> vertices, List<VertexNormal> normals, List<TextureUv> uvs, List<Group> groups) ExtractOBJData(string[] lines)
        {
            var enumerable = from line in lines
                let split = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).ToArray()
                where split.Any() && !split.First().StartsWith("#")
                select split;

            var vertices = new List<Vertex>();
            var normals = new List<VertexNormal>();
            var uvs = new List<TextureUv>();
            var currentGroup = new Group("unnamed");
            var groups = new List<Group>();


            foreach (var line in enumerable)
            {
                switch (line[0])
                {
                    case "v":
                        Debug.Assert(line.Length == 4);
                        vertices.Add(new Vertex
                        {
                            X = float.Parse(line[1]),
                            Y = float.Parse(line[2]),
                            Z = float.Parse(line[3])
                        });
                        break;
                    case "vn":
                        Debug.Assert(line.Length == 4);
                        normals.Add(new VertexNormal
                        {
                            X = float.Parse(line[1]),
                            Y = float.Parse(line[2]),
                            Z = float.Parse(line[3])
                        });
                        break;
                    case "vt":
                        uvs.Add(new TextureUv
                        {
                            U = float.Parse(line[1]),
                            V = float.Parse(line[2])
                            //Ignore w
                        });
                        break;
                    case "g":
                        currentGroup = new Group(line[1]);
                        groups.Add(currentGroup);

                        break;
                    case "f":
                        Debug.Assert(line.Length < 5);
                        var polygon = new Polygon();
                        for (var i = 1; i < line.Length; ++i)
                        {
                            var elements = line[i].Split("/");
                            var vertexIndex = int.Parse(elements[0]);
                            var textureUvIndex = int.Parse(elements[1]);
                            var normalIndex = int.Parse(elements[2]);
                            polygon.AddElement(new Element(vertexIndex, normalIndex, textureUvIndex));
                        }

                        currentGroup.AddPolygon(polygon);
                        break;
                }
            }

            return (vertices, normals, uvs, groups);
        }

        private static void SerializeJson(JsonTextWriter writer, JToken root, int indent = 0)
        {
            switch (root.Type)
            {
                case JTokenType.Object:
                    writer.WriteStartObject();
                    foreach (var child in root.Children<JProperty>())
                    {
                        writer.WritePropertyName(child.Name);
                        //Recurse.
                        SerializeJson(writer, child.Value);
                        //writer.WriteValue("");
                    }
                    writer.WriteEndObject();
                    break;
                case JTokenType.Array:
                    
                    writer.WriteStartArray();
                    var overrideIndenting = root.FirstOrDefault()?.Type != JTokenType.Array;
                    if (overrideIndenting)
                    {
                        writer.Formatting = Formatting.None;
                    }
                    foreach (var child in root)
                    {
                        SerializeJson(writer, child);
                    }
                    
                    writer.WriteEndArray();
                    if (overrideIndenting)
                    {
                        writer.Formatting = Formatting.Indented;
                    }
                    break;
                case JTokenType.String:
                    writer.WriteValue(root.Value<string>());
                    break;
                case JTokenType.Integer:
                    writer.WriteValue(root.Value<int>());
                    break;
                case JTokenType.Float:
                    writer.WriteValue(root.Value<float>());
                    break;
                default:
                    throw new NotImplementedException();
            }
        }
    }

    internal struct Vertex
    {
	    public float X;
	    public float Y;
        public float Z;
    }

    internal struct VertexNormal
    {
	    public float X;
	    public float Y;
	    public float Z;
    }

    internal struct TextureUv
    {
	    public float U;
		public float V;
    }

    internal class Group
    {
	    public readonly string Name;
		public List<Polygon> Polygons = new List<Polygon>();
	    public Group(string name)
	    {
		    Name = name;
	    }

	    public void AddPolygon(Polygon polygon)
	    {
		    Polygons.Add(polygon);
	    }

    }

    internal class Polygon
    {
	    public List<Element> Elements = new List<Element>();
        public void AddElement(Element element)
	    {
		    Elements.Add(element);
	    }
    }

    internal class Element
    {
        public readonly int VertexIndex;
        public readonly int TextureUvIndex;
        public readonly int NormalIndex;

	    public Element(int vertexIndex, int normalIndex, int textureUvIndex)
	    {
            VertexIndex = vertexIndex;
            TextureUvIndex = textureUvIndex;
            NormalIndex = normalIndex;
	    }
    }
}
