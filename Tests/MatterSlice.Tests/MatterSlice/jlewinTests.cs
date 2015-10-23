/*
Copyright (c) 2014, Lars Brubaker
All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this
   list of conditions and the following disclaimer.
2. Redistributions in binary form must reproduce the above copyright notice,
   this list of conditions and the following disclaimer in the documentation
   and/or other materials provided with the distribution.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR
ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
(INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

The views and conclusions contained in the software and documentation are those
of the authors and should not be interpreted as representing official policies,
either expressed or implied, of the FreeBSD Project.
*/

using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using MatterSlice.ClipperLib;
using System.Linq;
using System.Text.RegularExpressions;

namespace MatterHackers.MatterSlice.Tests
{
	[TestFixture, Category("MatterSlice.ExtrusionTests")]
	public class JlewinTests
	{
		[Test]
		public void ExtrusionWidthValid()
		{
			string sourceFile = TestUtlities.GetStlPath("HollowSquare");
			string outputPath = TestUtlities.GetTempGCodePath("HollowSquare.gcode");

			int sliceCount = 14;
			//int sliceCount = 3;

			for (int i = 1; i < sliceCount; i += 2)
			{
				var extrusionWidth = 0.1 * i;

				Console.WriteLine("Extruding at " + extrusionWidth);

				var config = new ConfigSettings()
				{
					extrusionWidth = extrusionWidth,
					infillPercent = 0,
					infillType = ConfigConstants.INFILL_TYPE.TRIANGLES,
					layerThickness = 0.2,
					numberOfTopLayers = 0,
					numberOfBottomLayers = 0,
					firstLayerExtrusionWidth = extrusionWidth,
					positionToPlaceObjectCenter = new DoublePoint(0, 0)
				};

				fffProcessor processor = new fffProcessor(config);

				processor.SetTargetFile(outputPath);
				processor.LoadStlFile(sourceFile);
				
				// slice and save it
				processor.DoProcessing();
				processor.finalize();

				Assert.True(AnalyzeGeneratedFile(outputPath, extrusionWidth));
			}

		}

		public bool AnalyzeGeneratedFile(string outputPath, double extrusionWidth)
		{
			string[] allLines = File.ReadAllLines(outputPath);

			Instruction lastMove = null;

			var allLayers = new List<List<Line>>();

			List<Line> moves = null;

			foreach (string line in allLines.Select(l => l.Trim()))
			{
				if (line.StartsWith("; LAYER:"))
				{
					moves = new List<Line>();
					allLayers.Add(moves);
				}
				else if (line.StartsWith(";"))
				{
					// Skip comment lines
					continue;
				}

				// Drop comments
				int pos = line.IndexOf(";");
				string lineClipped = pos == -1 ? line : line.Substring(0, pos);

				var instruction = new Instruction(Regex.Split(lineClipped.Trim(), "\\s+"));
				if ((instruction.Command == "G0" || instruction.Command == "G1") && !instruction.ExtrudeOnly)
				{
					if (lastMove != null)
					{
						moves.Add(new Line(lastMove, instruction));
					}

					lastMove = instruction;
				}
			}

			int expectedLineEdge = 5;
			double halfExtrusionWidth = extrusionWidth / 2;

			// Process all layers
			foreach (var layer in allLayers)
			{
				// Select vertical lines that have a midpoint with X > 0
				var filtered = layer.Where(l => l.Midpoint.X > 0 && l.Extrude && double.IsInfinity(l.Slope));

				// Order remaining lines by X, then select the closest line to 0
				var targetLine = filtered.OrderBy(l => l.Midpoint.X).First();

				// The expected X position is 5 + half the extrusion width
				double expectedX = expectedLineEdge + halfExtrusionWidth;

				Assert.IsTrue(expectedX == targetLine.Midpoint.X);
			}

			return true;
		}


		class Line
		{
			public Instruction Start { get; set; }
			public Instruction End { get; set; }
			public bool Extrude { get; set; }
			public DoublePoint Midpoint { get; set; }

			public double Slope { get; set; }

			public Line(Instruction start, Instruction end)
			{

				Start = start;
				End = end;
				Extrude = start.E != null || end.E != null;

				Midpoint = new DoublePoint()
				{
					X = (start.X + end.X) / 2,
					Y = (start.Y + end.Y) / 2,
				};

				Slope = (start.Y - end.Y) / (start.X - end.X);
			}

			public override string ToString()
			{
				return string.Format("{0},{1} to {2},{3} - ({4:0.##},{5:0.##}) [{6}]",
					Start.X,
					Start.Y,
					End.X,
					End.Y,
					Midpoint.X,
					Midpoint.Y,
					Slope);
			}
		}

		public class Instruction
		{
			public string Command { get; private set; }
			public double X { get; private set; }
			public double Y { get; private set; }
			public string F { get; private set; }
			public string E { get; private set; }
			public bool ExtrudeOnly { get; set; }

			public Instruction(IList<string> segments)
			{
				this.ExtrudeOnly = true;
				this.E = null;
				this.Command = segments[0];

				foreach (var v in segments.Skip(1))
				{
					string prop = v.Substring(0, 1);
					string val = v.Substring(1);

					switch (prop)
					{
						case "X":
							this.X = double.Parse(val);
							this.ExtrudeOnly = false;
							break;

						case "Y":
							this.Y = double.Parse(val);
							this.ExtrudeOnly = false;
							break;

						case "F":
							this.F = val;
							break;
						case "E":
							this.E = val;
							break;
					}
				}
			}
		}

	}
}