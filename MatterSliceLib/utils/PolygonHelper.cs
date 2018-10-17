/*
This file is part of MatterSlice. A commandline utility for
generating 3D printing GCode.

Copyright (C) 2013 David Braam
Copyright (c) 2014, Lars Brubaker

MatterSlice is free software: you can redistribute it and/or modify
it under the terms of the GNU Affero General Public License as
published by the Free Software Foundation, either version 3 of the
License, or (at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU Affero General Public License for more details.

You should have received a copy of the GNU Affero General Public License
along with this program.  If not, see <http://www.gnu.org/licenses/>.
*/

using System.Collections.Generic;
using System.IO;
using MSClipperLib;

namespace MatterHackers.MatterSlice
{
	using Polygon = List<IntPoint>;
	using Polygons = List<List<IntPoint>>;

	public static class PolygonHelper
	{
		public static IntPoint CenterOfMass(this Polygon polygon)
		{
			IntPoint center = new IntPoint();
			for (int positionIndex = 0; positionIndex < polygon.Count; positionIndex++)
			{
				center += polygon[positionIndex];
			}

			center /= polygon.Count;
			return center;
		}

		public static Polygon CreateConvexHull(this Polygon inPolygon)
		{
			return new Polygon(GrahamScan.GetConvexHull(inPolygon));
		}

		public static bool DescribesSameShape(this Polygon a, Polygon b)
		{
			if (a.Count != b.Count)
			{
				return false;
			}

			// find first same point
			for (int indexB = 0; indexB < b.Count; indexB++)
			{
				if (a[0] == b[indexB])
				{
					// check if any point are different
					for (int indexA = 1; indexA < a.Count; indexA++)
					{
						if (a[indexA] != b[(indexB + indexA) % b.Count])
						{
							return false;
						}
					}

					// they are all the same
					return true;
				}
			}

			return false;
		}

		public static IntPoint getBoundaryPointWithOffset(Polygon poly, int point_idx, long offset)
		{
			IntPoint p0 = poly[(point_idx > 0) ? (point_idx - 1) : (poly.size() - 1)];
			IntPoint p1 = poly[point_idx];
			IntPoint p2 = poly[(point_idx < (poly.size() - 1)) ? (point_idx + 1) : 0];

			IntPoint off0 = ((p1 - p0).Normal(1000)).CrossZ(); // 1.0 for some precision
			IntPoint off1 = ((p2 - p1).Normal(1000)).CrossZ(); // 1.0 for some precision
			IntPoint n = (off0 + off1).Normal(-offset);

			return p1 + n;
		}

		public static bool Inside(this Polygon polygon, IntPoint testPoint)
		{
			int positionOnPolygon = Clipper.PointInPolygon(testPoint, polygon);
			if (positionOnPolygon == 0) // not inside or on boundary
			{
				return false;
			}

			return true;
		}

		public static bool IsVertexConcave(this Polygon vertices, int vertex)
		{
			IntPoint current = vertices[vertex];
			IntPoint next = vertices[(vertex + 1) % vertices.Count];
			IntPoint previous = vertices[vertex == 0 ? vertices.Count - 1 : vertex - 1];

			IntPoint left = new IntPoint(current.X - previous.X, current.Y - previous.Y);
			IntPoint right = new IntPoint(next.X - current.X, next.Y - current.Y);

			long cross = (left.X * right.Y) - (left.Y * right.X);

			return cross < 0;
		}

		public static long MinX(this Polygon polygon)
		{
			long minX = long.MaxValue;
			foreach (var point in polygon)
			{
				if (point.X < minX)
				{
					minX = point.X;
				}
			}

			return minX;
		}

		public static void OptimizePolygon(this Polygon polygon)
		{
			IntPoint previousPoint = polygon[polygon.Count - 1];
			for (int i = 0; i < polygon.Count; i++)
			{
				IntPoint currentPoint = polygon[i];
				if ((previousPoint - currentPoint).IsShorterThen(10))
				{
					polygon.RemoveAt(i);
					i--;
				}
				else
				{
					IntPoint nextPoint;
					if (i < polygon.Count - 1)
					{
						nextPoint = polygon[i + 1];
					}
					else
					{
						nextPoint = polygon[0];
					}

					IntPoint diff0 = (currentPoint - previousPoint).SetLength(1000000);
					IntPoint diff2 = (currentPoint - nextPoint).SetLength(1000000);

					long d = diff0.Dot(diff2);
					if (d < -999999000000)
					{
						polygon.RemoveAt(i);
						i--;
					}
					else
					{
						previousPoint = currentPoint;
					}
				}
			}
		}

		public static bool Orientation(this Polygon polygon)
		{
			return Clipper.Orientation(polygon);
		}

		public static void Reverse(this Polygon polygon)
		{
			polygon.Reverse();
		}

		public static void SaveToGCode(this Polygon polygon, string filename)
		{
			double scale = 1000;
			StreamWriter stream = new StreamWriter(filename);
			stream.Write("; some gcode to look at the layer segments\n");
			int extrudeAmount = 0;
			double firstX = 0;
			double firstY = 0;
			for (int intPointIndex = 0; intPointIndex < polygon.Count; intPointIndex++)
			{
				double x = (double)(polygon[intPointIndex].X) / scale;
				double y = (double)(polygon[intPointIndex].Y) / scale;
				if (intPointIndex == 0)
				{
					firstX = x;
					firstY = y;
					stream.Write("G1 X{0} Y{1}\n", x, y);
				}
				else
				{
					stream.Write("G1 X{0} Y{1} E{2}\n", x, y, ++extrudeAmount);
				}
			}
			stream.Write("G1 X{0} Y{1} E{2}\n", firstX, firstY, ++extrudeAmount);

			stream.Close();
		}

		public static int size(this Polygon polygon)
		{
			return polygon.Count;
		}

		// true if p0 -> p1 -> p2 is strictly convex.
		private static bool convex3(long x0, long y0, long x1, long y1, long x2, long y2)
		{
			return (y1 - y0) * (x1 - x2) > (x0 - x1) * (y2 - y1);
		}

		private static bool convex3(IntPoint p0, IntPoint p1, IntPoint p2)
		{
			return convex3(p0.X, p0.Y, p1.X, p1.Y, p2.X, p2.Y);
		}

		// operator to sort IntPoint by y
		// (and then by X, where Y are equal)
		public class IntPointSorterYX : IComparer<IntPoint>
		{
			public virtual int Compare(IntPoint a, IntPoint b)
			{
				if (a.Y == b.Y)
				{
					return a.X.CompareTo(b.X);
				}
				else
				{
					return a.Y.CompareTo(b.Y);
				}
			}
		}
	}

	// Axis aligned boundary box
	public class Aabb
	{
		public IntPoint min, max;

		public Aabb()
		{
			min = new IntPoint(long.MinValue, long.MinValue);
			max = new IntPoint(long.MinValue, long.MinValue);
		}

		public Aabb(Polygons polys)
		{
			min = new IntPoint(long.MinValue, long.MinValue);
			max = new IntPoint(long.MinValue, long.MinValue);
			Calculate(polys);
		}

		public void Calculate(Polygons polys)
		{
			min = new IntPoint(long.MaxValue, long.MaxValue);
			max = new IntPoint(long.MinValue, long.MinValue);
			for (int i = 0; i < polys.Count; i++)
			{
				for (int j = 0; j < polys[i].Count; j++)
				{
					if (min.X > polys[i][j].X) min.X = polys[i][j].X;
					if (min.Y > polys[i][j].Y) min.Y = polys[i][j].Y;
					if (max.X < polys[i][j].X) max.X = polys[i][j].X;
					if (max.Y < polys[i][j].Y) max.Y = polys[i][j].Y;
				}
			}
		}

		public bool Hit(Aabb other)
		{
			if (max.X < other.min.X) return false;
			if (min.X > other.max.X) return false;
			if (max.Y < other.min.Y) return false;
			if (min.Y > other.max.Y) return false;
			return true;
		}
	}
}