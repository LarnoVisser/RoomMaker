using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;      // ✅ add this
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using DesignAutomationFramework;
using Newtonsoft.Json.Linq;

namespace RoomMaker
{
    // Entry point for Revit Design Automation
    public class App : IExternalDBApplication
    {
        public ExternalDBApplicationResult OnStartup(ControlledApplication app)
        {
            DesignAutomationBridge.DesignAutomationReadyEvent += OnDesignAutomationReady;
            return ExternalDBApplicationResult.Succeeded;
        }

        public ExternalDBApplicationResult OnShutdown(ControlledApplication app)
        {
            DesignAutomationBridge.DesignAutomationReadyEvent -= OnDesignAutomationReady;
            return ExternalDBApplicationResult.Succeeded;
        }

        private void OnDesignAutomationReady(object sender, DesignAutomationReadyEventArgs e)
        {
            try
            {
                DoJob(e.DesignAutomationData);
                e.Succeeded = true;
            }
            catch (Exception ex)
            {
                // In a real project, write to a log file in the working folder
                Console.WriteLine("Error in RoomMaker DoJob: " + ex);
                e.Succeeded = false;
            }
        }

        private void DoJob(DesignAutomationData data)
        {
            // The main Revit document (template) is already open
            Document doc = data.RevitDoc;
            if (doc == null)
                throw new InvalidOperationException("No Revit document is open.");

            // Working directory for Design Automation
            string workDir = Directory.GetCurrentDirectory();
            string jsonPath = Path.Combine(workDir, "room.json");

            if (!File.Exists(jsonPath))
                throw new FileNotFoundException("room.json not found in working directory", jsonPath);

            string json = File.ReadAllText(jsonPath);
            JObject root = JObject.Parse(json);
            JObject room = (JObject)root["room"];

            double length_m = (double)room["length_m"];
            double width_m  = (double)room["width_m"];
            double height_m = (double)room["height_m"];

            const double mToFt = 3.2808399;
            double L = length_m * mToFt;
            double W = width_m  * mToFt;
            double H = height_m * mToFt;

            using (Transaction tx = new Transaction(doc, "Create Room from JSON"))
            {
                tx.Start();

                // 1. Find or create a level at elevation 0
                Level level = new FilteredElementCollector(doc)
                    .OfClass(typeof(Level))
                    .Cast<Level>()
                    .FirstOrDefault(l => Math.Abs(l.Elevation) < 0.001);

                if (level == null)
                {
                    level = Level.Create(doc, 0.0);
                    level.Name = "Level 0";
                }

                // 2. Get a basic wall type
                WallType wallType = new FilteredElementCollector(doc)
                    .OfClass(typeof(WallType))
                    .Cast<WallType>()
                    .FirstOrDefault(wt => wt.Kind == WallKind.Basic);

                if (wallType == null)
                    throw new InvalidOperationException("No basic WallType found in document.");

                // 3. Get a floor type
                FloorType floorType = new FilteredElementCollector(doc)
                    .OfClass(typeof(FloorType))
                    .Cast<FloorType>()
                    .FirstOrDefault();

                if (floorType == null)
                    throw new InvalidOperationException("No FloorType found in document.");

                // 4. Define rectangle corners (0,0) to (L,W)
                XYZ p1 = new XYZ(0, 0, 0);
                XYZ p2 = new XYZ(L, 0, 0);
                XYZ p3 = new XYZ(L, W, 0);
                XYZ p4 = new XYZ(0, W, 0);

                // 5. Create walls
                Wall.Create(doc, Line.CreateBound(p1, p2), wallType.Id, level.Id, H, 0, false, false);
                Wall.Create(doc, Line.CreateBound(p2, p3), wallType.Id, level.Id, H, 0, false, false);
                Wall.Create(doc, Line.CreateBound(p3, p4), wallType.Id, level.Id, H, 0, false, false);
                Wall.Create(doc, Line.CreateBound(p4, p1), wallType.Id, level.Id, H, 0, false, false);

                // 6. Create floor (Revit 2023+ API – use Floor.Create with CurveLoop)
                CurveLoop floorLoop = new CurveLoop();
                floorLoop.Append(Line.CreateBound(p1, p2));
                floorLoop.Append(Line.CreateBound(p2, p3));
                floorLoop.Append(Line.CreateBound(p3, p4));
                floorLoop.Append(Line.CreateBound(p4, p1));
                
                IList<CurveLoop> loops = new List<CurveLoop> { floorLoop };
                
                Floor floor = Floor.Create(doc, loops, floorType.Id, level.Id);

                tx.Commit();
            }

            // Design Automation will handle saving result.rvt based on Activity args
        }
    }
}
