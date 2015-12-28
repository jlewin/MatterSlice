/*
This file is part of MatterSlice. A commandline utility for
generating 3D printing GCode.

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

using MatterSlice.ClipperLib;
using System;
using System.Collections.Generic;

// TODO:
// Create extra upward support for small features (tip of a rotated box)
// sparse write the support layers so they are easier to remove
// check frost morn, should have support under unsupported parts

// DONE:
// make sure all air gapped layers are written after ALL extruder normal layers
// Make the on model material be air gapped
// Offset the output data to account for nozzle diameter (currently they are just the outlines not the extrude positions)
// Make skirt consider these outlines
// Make raft consider these outlines
// Make sure we work correctly with the support extruder set.
// Make from bed only work (no internal support)
// Fix extra extruder material on top of interface layer

namespace MatterHackers.MatterSlice
{
	using Polygon = List<IntPoint>;
	using Polygons = List<List<IntPoint>>;

	public class NewSupport
	{
		readonly static double cleanDistance_um = 10;

		internal List<Polygons> allPartOutlines = new List<Polygons>();
		internal List<Polygons> allPotentialSupportOutlines = new List<Polygons>();
		internal List<Polygons> allRequiredSupportOutlines = new List<Polygons>();
		internal List<Polygons> easyGrabDistanceOutlines = new List<Polygons>();
		//List<Polygons> pushedUpTopOutlines = new List<Polygons>();
		internal List<Polygons> airGappedBottomOutlines = new List<Polygons>();
		internal List<Polygons> supportOutlines = new List<Polygons>();
		internal List<Polygons> interfaceLayers = new List<Polygons>();

		double grabDistanceMm;

		public Polygons GetRequiredSupportAreas(int layerIndex)
		{
			layerIndex--;
			if (layerIndex < allRequiredSupportOutlines.Count && layerIndex >= 0)
			{
				return allRequiredSupportOutlines[layerIndex];
			}

			return new Polygons();
		}

		public NewSupport(ConfigSettings config, List<ExtruderLayers> Extruders, double grabDistanceMm)
		{
			this.grabDistanceMm = grabDistanceMm;
			// create starting support outlines
			allPartOutlines = CalculateAllPartOutlines(config, Extruders);

			allPotentialSupportOutlines = FindAllPotentialSupportOutlines(allPartOutlines, config);

			allRequiredSupportOutlines = RemoveSelfSupportedSections(allPotentialSupportOutlines, config);

			if (!config.generateInternalSupport)
			{
				allRequiredSupportOutlines = RemoveSupportFromInternalSpaces(allRequiredSupportOutlines, allPartOutlines);
			}

			easyGrabDistanceOutlines = ExpandToEasyGrabDistance(allRequiredSupportOutlines, (int)(grabDistanceMm * 1000));

			//pushedUpTopOutlines = PushUpTops(easyGrabDistanceOutlines, numLayers, config);

			interfaceLayers = CreateInterfaceLayers(easyGrabDistanceOutlines, config.supportInterfaceLayers);
			interfaceLayers = ClipToXyDistance(interfaceLayers, allPartOutlines, config);

			supportOutlines = AccumulateDownPolygons(easyGrabDistanceOutlines, allPartOutlines);
			supportOutlines = ClipToXyDistance(supportOutlines, allPartOutlines, config);

			// remove the interface layers from the normal support layers
			supportOutlines = CalculateDifferencePerLayer(supportOutlines, interfaceLayers);

			airGappedBottomOutlines = CreateAirGappedBottomLayers(supportOutlines, allPartOutlines);
			// remove the airGappedBottomOutlines layers from the normal support layers
			supportOutlines = CalculateDifferencePerLayer(supportOutlines, airGappedBottomOutlines);
		}

		private static List<Polygons> RemoveSupportFromInternalSpaces(List<Polygons> inputPolys, List<Polygons> allPartOutlines)
		{
			int numLayers = inputPolys.Count;

			Polygons accumulatedLayers = new Polygons();
			for (int layerIndex = 0; layerIndex < numLayers; layerIndex++)
			{
				accumulatedLayers = accumulatedLayers.CreateUnion(allPartOutlines[layerIndex]);
				accumulatedLayers = Clipper.CleanPolygons(accumulatedLayers, cleanDistance_um);

				inputPolys[layerIndex] = inputPolys[layerIndex].CreateDifference(accumulatedLayers);
				inputPolys[layerIndex] = Clipper.CleanPolygons(inputPolys[layerIndex], cleanDistance_um);
			}

			return inputPolys;
		}

		static List<Polygons> CreateEmptyPolygons(int numLayers)
		{
			List<Polygons> polygonsList = new List<Polygons>();
			for (int i = 0; i < numLayers; i++)
			{
				polygonsList.Add(new Polygons());
			}

			return polygonsList;
		}

		private static List<Polygons> CalculateAllPartOutlines(ConfigSettings config, List<ExtruderLayers> Extruders)
		{
			int numLayers = Extruders[0].Layers.Count;

			List<Polygons> allPartOutlines = CreateEmptyPolygons(numLayers);

			foreach (var extruder in Extruders)
			{
				// calculate the combined outlines for everything
				for (int layerIndex = numLayers - 1; layerIndex >= 0; layerIndex--)
				{
					allPartOutlines[layerIndex] = allPartOutlines[layerIndex].CreateUnion(extruder.Layers[layerIndex].AllOutlines);
				}
			}

			return allPartOutlines;
		}

		private static List<Polygons> FindAllPotentialSupportOutlines(List<Polygons> inputPolys, ConfigSettings config)
		{
			int numLayers = inputPolys.Count;
			List<Polygons> allPotentialSupportOutlines = CreateEmptyPolygons(numLayers);
			// calculate all the non-supported areas
			for (int layerIndex = numLayers - 2; layerIndex >= 0; layerIndex--)
			{
				Polygons aboveLayerPolys = inputPolys[layerIndex + 1];
				Polygons curLayerPolys = inputPolys[layerIndex];
				Polygons supportedAreas = aboveLayerPolys.CreateDifference(curLayerPolys);
				allPotentialSupportOutlines[layerIndex] = Clipper.CleanPolygons(supportedAreas, cleanDistance_um);
			}

			return allPotentialSupportOutlines;
		}

		private static List<Polygons> RemoveSelfSupportedSections(List<Polygons> inputPolys, ConfigSettings config)
		{
			int numLayers = inputPolys.Count;

			List<Polygons> allRequiredSupportOutlines = CreateEmptyPolygons(numLayers);
			// calculate all the non-supported areas
			for (int layerIndex = numLayers - 1; layerIndex > 0; layerIndex--)
			{
				if (inputPolys[layerIndex - 1].Count > 0)
				{
					if (inputPolys[layerIndex].Count > 0)
					{
						allRequiredSupportOutlines[layerIndex] = inputPolys[layerIndex].Offset(-config.extrusionWidth_um / 2);
						allRequiredSupportOutlines[layerIndex] = allRequiredSupportOutlines[layerIndex].Offset(config.extrusionWidth_um / 2);
						allRequiredSupportOutlines[layerIndex] = Clipper.CleanPolygons(allRequiredSupportOutlines[layerIndex], cleanDistance_um);
					}
				}
				else
				{
					allRequiredSupportOutlines[layerIndex] = inputPolys[layerIndex].DeepCopy();
				}
			}

			return allRequiredSupportOutlines;
		}

		private static List<Polygons> ExpandToEasyGrabDistance(List<Polygons> inputPolys, long grabDistance_um)
		{
			int numLayers = inputPolys.Count;

			List<Polygons> easyGrabDistanceOutlines = CreateEmptyPolygons(numLayers);
			for (int layerIndex = numLayers - 1; layerIndex >= 0; layerIndex--)
			{
				Polygons curLayerPolys = inputPolys[layerIndex];
				easyGrabDistanceOutlines[layerIndex] = Clipper.CleanPolygons(curLayerPolys.Offset(grabDistance_um), cleanDistance_um);
			}

			return easyGrabDistanceOutlines;
		}

		public Polygons GetBedOutlines()
		{
			return supportOutlines[0].CreateUnion(interfaceLayers[0]);
		}

		private static List<Polygons> PushUpTops(List<Polygons> inputPolys, ConfigSettings config)
		{
			int numLayers = inputPolys.Count;

			return inputPolys;
			int layersFor2Mm = 2000 / config.layerThickness_um;
			List<Polygons> pushedUpPolys = CreateEmptyPolygons(numLayers);
			for (int layerIndex = numLayers - 1; layerIndex >= 0; layerIndex--)
			{
				for (int layerToAddToIndex = Math.Min(layerIndex + layersFor2Mm, numLayers - 1); layerToAddToIndex >= 0; layerToAddToIndex--)
				{
				}

				Polygons curLayerPolys = inputPolys[layerIndex];
				pushedUpPolys[layerIndex] = Clipper.CleanPolygons(curLayerPolys.Offset(config.extrusionWidth_um + config.supportXYDistance_um), cleanDistance_um);
			}

			return pushedUpPolys;
		}

		private static List<Polygons> AccumulateDownPolygons(List<Polygons> inputPolys, List<Polygons> allPartOutlines)
		{
			int numLayers = inputPolys.Count;

			List<Polygons> allDownOutlines = CreateEmptyPolygons(numLayers);
			for (int layerIndex = numLayers - 2; layerIndex >= 0; layerIndex--)
			{
				Polygons aboveRequiredSupport = inputPolys[layerIndex + 1];

				// get all the polygons above us
				Polygons accumulatedAbove = allDownOutlines[layerIndex + 1].CreateUnion(aboveRequiredSupport);

				// add in the support on this level
				Polygons curRequiredSupport = inputPolys[layerIndex];
				Polygons totalSupportThisLayer = accumulatedAbove.CreateUnion(curRequiredSupport);

				// remove the solid polygons on this level
				Polygons remainingAbove = totalSupportThisLayer.CreateDifference(allPartOutlines[layerIndex]);

				allDownOutlines[layerIndex] = Clipper.CleanPolygons(remainingAbove, cleanDistance_um);
			}

			return allDownOutlines;
		}

		private static List<Polygons> CreateAirGappedBottomLayers(List<Polygons> inputPolys, List<Polygons> allPartOutlines)
		{
			int numLayers = inputPolys.Count;

			List<Polygons> airGappedBottoms = CreateEmptyPolygons(numLayers);
			for (int layerIndex = 1; layerIndex < numLayers; layerIndex++)
			{
				Polygons belowOutlines = allPartOutlines[layerIndex - 1];

				Polygons curRequiredSupport = inputPolys[layerIndex];
				Polygons airGapArea = belowOutlines.CreateIntersection(curRequiredSupport);

				airGappedBottoms[layerIndex] = airGapArea;
			}

			return airGappedBottoms;
		}

		private static List<Polygons> CreateInterfaceLayers(List<Polygons> inputPolys, int numInterfaceLayers)
		{
			int numLayers = inputPolys.Count;

			List<Polygons> allInterfaceLayers = CreateEmptyPolygons(numLayers);
			if (numInterfaceLayers > 0)
			{
				for (int layerIndex = 0; layerIndex < numLayers; layerIndex++)
				{
					Polygons accumulatedAbove = inputPolys[layerIndex].DeepCopy();

					for (int addIndex = layerIndex + 1; addIndex < Math.Min(layerIndex + numInterfaceLayers, numLayers - 2); addIndex++)
					{
						accumulatedAbove = accumulatedAbove.CreateUnion(inputPolys[addIndex]);
						accumulatedAbove = Clipper.CleanPolygons(accumulatedAbove, cleanDistance_um);
					}

					allInterfaceLayers[layerIndex] = accumulatedAbove;
				}
			}

			return allInterfaceLayers;
		}

		private static List<Polygons> ClipToXyDistance(List<Polygons> inputPolys, List<Polygons> allPartOutlines, ConfigSettings config)
		{
			int numLayers = inputPolys.Count;

			List<Polygons> clippedToXyOutlines = CreateEmptyPolygons(numLayers);
			for (int layerIndex = numLayers - 2; layerIndex >= 0; layerIndex--)
			{
				Polygons curRequiredSupport = inputPolys[layerIndex];
				Polygons expandedlayerPolys = allPartOutlines[layerIndex].Offset(config.supportXYDistance_um);
				Polygons totalSupportThisLayer = curRequiredSupport.CreateDifference(expandedlayerPolys);

				clippedToXyOutlines[layerIndex] = Clipper.CleanPolygons(totalSupportThisLayer, cleanDistance_um);
			}

			return clippedToXyOutlines;
		}

		private static List<Polygons> CalculateDifferencePerLayer(List<Polygons> inputPolys, List<Polygons> outlinesToRemove)
		{
			int numLayers = inputPolys.Count;

			List<Polygons> diferenceLayers = CreateEmptyPolygons(numLayers);
			for (int layerIndex = numLayers - 2; layerIndex >= 0; layerIndex--)
			{
				Polygons curRequiredSupport = inputPolys[layerIndex];
				Polygons totalSupportThisLayer = curRequiredSupport.CreateDifference(outlinesToRemove[layerIndex]);

				diferenceLayers[layerIndex] = Clipper.CleanPolygons(totalSupportThisLayer, cleanDistance_um);
			}

			return diferenceLayers;
		}

		public void QueueNormalSupportLayer(ConfigSettings config, GCodePlanner gcodeLayer, int layerIndex, GCodePathConfig supportNormalConfig, GCodePathConfig supportInterfaceConfig)
		{
			// normal support
			Polygons currentSupportOutlines = supportOutlines[layerIndex];
			currentSupportOutlines = currentSupportOutlines.Offset(-config.extrusionWidth_um / 2);
			List<Polygons> supportIslands = currentSupportOutlines.ProcessIntoSeparatIslands();

			foreach (Polygons islandOutline in supportIslands)
			{
				Polygons islandInfillLines = new Polygons();
				// render a grid of support
				gcodeLayer.QueuePolygonsByOptimizer(islandOutline, supportNormalConfig);
				Polygons infillOutline = islandOutline.Offset(-config.extrusionWidth_um / 2);
				switch (config.supportType)
				{
					case ConfigConstants.SUPPORT_TYPE.GRID:
						Infill.GenerateGridInfill(config, infillOutline, ref islandInfillLines, config.supportInfillStartingAngle, config.supportLineSpacing_um);
						break;

					case ConfigConstants.SUPPORT_TYPE.LINES:
						Infill.GenerateLineInfill(config, infillOutline, ref islandInfillLines, config.supportInfillStartingAngle, config.supportLineSpacing_um);
						break;
				}
				gcodeLayer.QueuePolygonsByOptimizer(islandInfillLines, supportNormalConfig);
			}

			// interface
			Polygons currentInterfaceOutlines = interfaceLayers[layerIndex].Offset(-config.extrusionWidth_um / 2);
			Polygons supportLines = new Polygons();
			Infill.GenerateLineInfill(config, currentInterfaceOutlines, ref supportLines, config.supportInfillStartingAngle + 90, config.extrusionWidth_um);
			gcodeLayer.QueuePolygonsByOptimizer(supportLines, supportInterfaceConfig);
		}

		public void QueueAirGappedBottomLayer(ConfigSettings config, GCodePlanner gcodeLayer, int layerIndex, GCodePathConfig supportNormalConfig)
		{
			// normal support
			Polygons currentAirGappedBottoms = airGappedBottomOutlines[layerIndex];
			currentAirGappedBottoms = currentAirGappedBottoms.Offset(-config.extrusionWidth_um / 2);
			List<Polygons> supportIslands = currentAirGappedBottoms.ProcessIntoSeparatIslands();

			foreach (Polygons islandOutline in supportIslands)
			{
				Polygons islandInfillLines = new Polygons();
				// render a grid of support
				gcodeLayer.QueuePolygonsByOptimizer(islandOutline, supportNormalConfig);
				Polygons infillOutline = islandOutline.Offset(-config.extrusionWidth_um / 2);
				switch (config.supportType)
				{
					case ConfigConstants.SUPPORT_TYPE.GRID:
						Infill.GenerateGridInfill(config, infillOutline, ref islandInfillLines, config.supportInfillStartingAngle, config.supportLineSpacing_um);
						break;

					case ConfigConstants.SUPPORT_TYPE.LINES:
						Infill.GenerateLineInfill(config, infillOutline, ref islandInfillLines, config.supportInfillStartingAngle, config.supportLineSpacing_um);
						break;
				}
				gcodeLayer.QueuePolygonsByOptimizer(islandInfillLines, supportNormalConfig);
			}
		}
	}
}