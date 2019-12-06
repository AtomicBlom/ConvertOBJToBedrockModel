using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using CommandLine;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ConvertOBJToBedrockModel
{
	class Options {
		[Option('i', "input", Required=true, HelpText="The OBJ file to read")]
		public string Input { get; set;}

		[Option('o', "output", Required=false, HelpText="The output file to write, if omitted, it will be written to the console.")]
		public string Output {get; set;}

		[Option('n', "name", Required=false, HelpText="The model name, if not specified, it will be inferred from the file")]
		public string Name { get; set;}

		[Option("inverty", Required=false, HelpText="inverts the model's Y Coordinates (useful for converting java entity models to bedrock)")]
		public bool InvertY { get; set; }

		[Option("swizzleyz", Required=false, HelpText="Swaps the model's Y and Z coordinates")]
		public bool SwizzleYZ { get; set; }
		
		[Option("fliptexture", Required=false, HelpText="Flips the model's texture V Coordinates useful if the obj was targeting OpenGL")]
		public bool FlipTexture { get; set; }

		[Option("scale", Required=false, Default=1, HelpText="Sets the scale of the model")]
		public float Scale { get; set;} = 1;

		[Option("translatey", Required=false, Default=0, HelpText="Translates the model along the Y axis")]
		public float TranslateY { get; set;} = 0;
	}

	class Program
	{
		static int Main(string[] args)
		{
			Parser.Default.ParseArguments<Options>(args)
				.WithParsed<Options>(o => {
					var lines = File.ReadAllLines(o.Input);

					var name = string.IsNullOrWhiteSpace(o.Name) ? new FileInfo(o.Input).Name : o.Name;

					var (vertices, normals, uvs, groups) = ExtractOBJData(lines);

					foreach (var vertex in vertices)
					{
						if (o.InvertY)
						{
							vertex.Y = -vertex.Y;
						}

						vertex.X = vertex.X * o.Scale;
						vertex.Y = vertex.Y * o.Scale + o.TranslateY;
						vertex.Z = vertex.Z * o.Scale;
					}

					foreach (var polygon in groups.SelectMany(g => g.Polygons))
					{
						if (o.SwizzleYZ)
						{
							(polygon.Elements[1], polygon.Elements[2]) = (polygon.Elements[2], polygon.Elements[1]);
						}
					}

					foreach (var textureUv in uvs)
					{
						if (o.FlipTexture)
						{
							textureUv.V = 1 - textureUv.V;
						}

						// if (normalizeUVs)
						// {
						// 	textureUv.U *= 1f;
						// 	textureUv.V *= 1f;
						// 	textureUv.U = 0 - textureUv.U;
						// 	textureUv.V = 0 - textureUv.V;
						// }
					}

					var root = CreateEntityModel(name, groups, vertices, normals, uvs);

					//Strictly controlled JSON formatting.
					var stringWriter = new StringWriter();
					var writer = new JsonTextWriter(stringWriter) {Formatting = Formatting.Indented};
					SerializeJson(writer, root);
					
					var result = stringWriter.ToString();
					
					if (!string.IsNullOrWhiteSpace(o.Output)) {
						File.WriteAllText(o.Output, result);
					} else {
						Console.WriteLine(result);
					}
				});

			return 0;
		}

		private static JObject CreateEntityModel(string name, IEnumerable<Group> groups, IReadOnlyList<Vertex> vertices, IReadOnlyList<VertexNormal> normals, IReadOnlyList<TextureUv> uvs)
		{
			var bones = new JArray
			{
				new JObject
				{
					{ "name", "root" }
				}
			};

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
					{"parent", "root" },
					{"pivot", new JArray
						{
							new JValue(0),
							new JValue(0),
							new JValue(0)
						}
					},
					{"poly_mesh", new JObject
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
				{"texturewidth", 1}, //TODO: Resolve this
				{"textureheight", 1},
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
						var vertex = new Vertex
						{
							X = float.Parse(line[1]),
							Y = float.Parse(line[2]),
							Z = float.Parse(line[3])
						};

						vertices.Add(vertex);
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
					case "o": //For the sake of bedrock, treat groups and objects as the same thing.
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
					var overrideIndenting = root.FirstOrDefault()?.Type != JTokenType.Array && root.FirstOrDefault()?.Type != JTokenType.Object;
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

	internal class Vertex
	{
		public float X;
		public float Y;
		public float Z;
	}

	internal class VertexNormal
	{
		public float X;
		public float Y;
		public float Z;
	}

	internal class TextureUv
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
