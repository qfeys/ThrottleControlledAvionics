﻿//  Author:
//       Allis Tauri <allista@gmail.com>
//
//  Copyright (c) 2016 Allis Tauri
//
// This work is licensed under the Creative Commons Attribution-ShareAlike 4.0 International License. 
// To view a copy of this license, visit http://creativecommons.org/licenses/by-sa/4.0/ 
// or send a letter to Creative Commons, PO Box 1866, Mountain View, CA 94042, USA.
//
using System;
using System.Collections.Generic;
using UnityEngine;
using AT_Utils;

namespace ThrottleControlledAvionics
{
	public class LandingTrajectory : TargetedTrajectory
	{
		public double TargetAltitude;
		public double LandingAngle;
		public WayPoint SurfacePoint { get; private set; }

		public double VslStartLat { get; private set; }
		public double VslStartLon { get; private set; }

		public double DeltaLat { get; private set; }
		public double DeltaLon { get; private set; }

		/// <summary>
		/// Radial difference between the target and the landing site in degrees.
		/// </summary>
		public double DeltaR { get; private set; } = 180;

		Vector3d brake_delta_v;
		public override Vector3d BrakeDeltaV { get { return brake_delta_v; } }
		public float  BrakeFuel { get; protected set; }
		public float  BrakeDuration;
		public double BrakeStartUT { get; private set; }
		public double BrakeEndUT { get; private set; }
		public double BrakeEndDeltaAlt { get; private set; }

		public double TimeToSurface { get { return AtTargetUT-VSL.Physics.UT; } }

		public LandingTrajectory(VesselWrapper vsl, Vector3d dV, double startUT, 
		                         WayPoint target, double target_altitude = 0, bool with_brake = true)
			: base(vsl, dV, startUT, target)
		{
			TargetAltitude = target_altitude;
			update(with_brake);
		}

		void update_from_orbit(Orbit orb, double UT)
		{
			//calculate the position of a landing site
			if(orb.ApA <= TargetAltitude) 
				AtTargetUT = orb.StartUT+(orb.ApAhead()? orb.timeToAp : 1);
			else if(orb.PeA < TargetAltitude) 
				AtTargetUT = TrajectoryCalculator.NearestRadiusUT(orb, Body.Radius+TargetAltitude, UT);
			else AtTargetUT = orb.StartUT+orb.timeToPe;
			TransferTime = AtTargetUT-StartUT;
			AtTargetPos = orb.getRelativePositionAtUT(AtTargetUT);
			AtTargetVel = orb.getOrbitalVelocityAtUT(AtTargetUT);
			SurfacePoint = new WayPoint((TrajectoryCalculator.BodyRotationAtdT(Body, -TimeToSurface)*AtTargetPos).xzy+Body.position, Body);
			SurfacePoint.Pos.SetAlt2Surface(Body);
			SurfacePoint.Name = "Landing Site";
		}

		void ClampBrakeDeltaV()
		{
			var dVm = brake_delta_v.magnitude;
			if(dVm > 0) 
			{
				var fuel = VSL.Engines.GetAvailableFuelMass()-ManeuverFuel;
				if(fuel <= 0) brake_delta_v = Vector3d.zero;
				else
				{
					BrakeFuel = VSL.Engines
						.FuelNeededAtAlt((float)dVm, (float)(BrakeEndDeltaAlt+TargetAltitude));
					if(BrakeFuel > fuel)
					{
						brake_delta_v = brake_delta_v*VSL.Engines.DeltaV((float)fuel)/dVm;
						BrakeFuel = fuel;
					}
				}
			}
			else BrakeFuel = 0;
		}

		void SetBrakeEndUT(double UT)
		{ 
			BrakeEndUT = UT;
			BrakeEndDeltaAlt = Orbit.getRelativePositionAtUT(BrakeEndUT).magnitude-Body.Radius-TargetAltitude; 
		}

		void SetBrakeDeltaV(Vector3d dV)
		{
			brake_delta_v = dV;
			ClampBrakeDeltaV();
		}

		void update(bool with_brake)
		{
			update_from_orbit(Orbit, StartUT);
			LandingAngle = 90-Vector3d.Angle(AtTargetPos, -AtTargetVel);
			//correct for brake maneuver
			if(with_brake)
			{
				//calculate vertical brake time
				var rotation_time = VSL.Torque.NoEngines? 
					VSL.Torque.NoEngines.MinRotationTime(90) :
					VSL.Torque.MaxPossible.RotationTime(90, 0.1f);
				SetBrakeEndUT(Math.Max(AtTargetUT-GLB.LTRJ.CorrectionOffset+rotation_time, StartUT));
				SetBrakeDeltaV(Vector3d.Project(AtTargetVel, AtTargetPos));
				if(BrakeFuel > 0)
				{
					var dV = (float)brake_delta_v.magnitude;
					BrakeDuration = VSL.Engines.TTB(dV, 
					                                Utils.ClampL(VSL.Engines.MaxThrustM - VSL.Physics.StG*VSL.Physics.M, 0.1f), 
					                                VSL.Engines.MaxMassFlow, ThrottleControl.NextThrottle(dV, 1, VSL));
					//add 90deg turn time to face the ground
					BrakeDuration += rotation_time;
					SetBrakeEndUT(Math.Max(AtTargetUT-Mathf.Max(GLB.LTRJ.CorrectionOffset, BrakeDuration*1.1f), StartUT));
					SetBrakeDeltaV(-0.9*Orbit.getOrbitalVelocityAtUT(BrakeEndUT));
					BrakeStartUT = Math.Max(BrakeEndUT-MatchVelocityAutopilot.BrakingOffset((float)BrakeDeltaV.magnitude, VSL, out BrakeDuration), StartUT);
					update_from_orbit(TrajectoryCalculator.NewOrbit(Orbit, BrakeDeltaV, BrakeEndUT), BrakeEndUT);
				}
				else 
				{
					BrakeStartUT = BrakeEndUT;	
					BrakeDuration = 0;
				}
			}
			else
			{
				BrakeEndUT = TrajectoryCalculator.FlyAboveUT(Orbit, Target.RelOrbPos(Body), StartUT);
				var tPos = Orbit.getRelativePositionAtUT(BrakeEndUT).normalized*(Body.Radius+TargetAltitude);
				SetBrakeDeltaV(-(Orbit.getOrbitalVelocityAtUT(BrakeEndUT) + Vector3d.Cross(Body.zUpAngularVelocity, tPos)));
				if(BrakeFuel > 0)
					BrakeStartUT = BrakeEndUT-MatchVelocityAutopilot.BrakingOffset((float)BrakeDeltaV.magnitude, VSL, out BrakeDuration);
				else
				{
					BrakeStartUT = BrakeEndUT;
					BrakeDuration = 0;
				}
			}
			//compute vessel coordinates at maneuver start
			if(VSL.LandedOrSplashed)
			{
				VslStartLat = Utils.ClampAngle(VSL.vessel.latitude);
				VslStartLon = Utils.ClampAngle(VSL.vessel.longitude);
			}
			else
			{
				var start_pos = (TrajectoryCalculator.BodyRotationAtdT(Body, -TimeToStart)*StartPos).xzy + Body.position;
				VslStartLat = Utils.ClampAngle(Body.GetLatitude(start_pos));
				VslStartLon = Utils.ClampAngle(Body.GetLongitude(start_pos));
			}
			//compute distance to target
			DistanceToTarget = Target.AngleTo(SurfacePoint)*Body.Radius;
			//compute distance in lat-lon coordinates
			DeltaLat = Utils.AngleDelta(SurfacePoint.Pos.Lat, Target.Pos.Lat)*
				Math.Sign(Utils.AngleDelta(Utils.ClampAngle(VslStartLat), SurfacePoint.Pos.Lat));
			DeltaLon = Utils.AngleDelta(SurfacePoint.Pos.Lon, Target.Pos.Lon)*
				Math.Sign(Utils.AngleDelta(Utils.ClampAngle(VslStartLon), SurfacePoint.Pos.Lon));
			//compute distance in radial coordinates
			DeltaFi = 90-Vector3d.Angle(Orbit.GetOrbitNormal(),
			                            TrajectoryCalculator.BodyRotationAtdT(Body, TimeToSurface) * 
			                            Body.GetRelSurfacePosition(Target.Pos.Lat, Target.Pos.Lon, TargetAltitude).xzy);
			DeltaR = Utils.RadDelta(SurfacePoint.AngleTo(VslStartLat, VslStartLon), Target.AngleTo(VslStartLat, VslStartLon))*Mathf.Rad2Deg;
		}

		public List<AtmosphericConditions> GetAtmosphericCurve(double dT)
		{
			if(!Body.atmosphere) return null;
			var atmoR = Body.Radius+Body.atmosphereDepth;
			var startUT = StartPos.magnitude < atmoR? StartUT : TrajectoryCalculator.NearestRadiusUT(Orbit, atmoR, StartUT);
			if(startUT > BrakeEndUT) return null;
			var samples = (int)Math.Ceiling((BrakeEndUT-startUT)/dT)+1;
			var curve = new List<AtmosphericConditions>(samples);
			dT = (BrakeEndUT-startUT)/samples;
			for(int i = 1; i <= samples; i++)
			{
				var cond = new AtmosphericConditions(Orbit, startUT+dT*i);
				cond.Duration = dT;
				curve.Add(cond);
			}
			return curve;
		}

		public Vector3d GetOrbitVelocityAtSurface()
		{ return Orbit.getOrbitalVelocityAtUT(AtTargetUT); }

		public override void UpdateOrbit(Orbit current)
		{
			base.UpdateOrbit(current);
			update(false);
		}

		public void UpdateOrbit(Orbit current, bool with_brake)
		{
			base.UpdateOrbit(current);
			update(with_brake);
		}

		public override string ToString()
		{
			return base.ToString()+
				Utils.Format("\nLanding Site: {},\n" +
				             "TimeToSurface: {} s\n" +
		                     "Delta R: {} deg\n" +
				             "Delta Lat: {} deg, Delta Lon: {} deg\n" +
				             "Brake DeltaV: {}\n" +
				             "Brake Duration: {} s, Time to Brake: {} s\n" +
				             "Brake Fuel: {}\n" +
				             "BrakeEnd Altitude {} m\n" +
				             "Atmo Conditions: {}\n" +
				             "Landing Angle {} deg",
				             SurfacePoint, TimeToSurface,
				             DeltaR, DeltaLat, DeltaLon,
				             BrakeDeltaV, BrakeDuration, BrakeStartUT-VSL.Physics.UT, BrakeFuel, BrakeEndDeltaAlt,
				             GetAtmosphericCurve(5),
				             LandingAngle);
		}
	}

	public class AtmosphericConditions
	{
		public double UT = -1;
		public double Duration;

		public double Altitude;
		public double Speed;

		public bool   Atmosphere;
		public double Pressure;
		public double Density;
		public double AtmosphericTemperature;

		public double DynamicPressure;
		public double ShockTemperature;
		public double ConvectiveCoefficient;

		public AtmosphericConditions(double UT)
		{ this.UT = UT; }


//		ptd.finalCoeff = this.convectiveCoefficient * ptd.convectionArea * 0.001 * part.heatConvectiveConstant * ptd.convectionCoeffMultiplier;
//		ptd.finalCoeff = Math.Min(ptd.finalCoeff, part.skinThermalMass * part.skinExposedAreaFrac);

		public AtmosphericConditions(Orbit orb, double UT) : this(UT)
		{
			var Body = orb.referenceBody;
			if(!Body.atmosphere) return;
			var pos = orb.getRelativePositionAtUT(UT);
			Altitude = pos.magnitude-Body.Radius;
			Pressure = Body.GetPressure(Altitude);
			if(Pressure > 0)
			{
				Speed = orb.getOrbitalSpeedAtRelativePos(pos);
				Atmosphere = true;
				AtmosphericTemperature = Body.GetTemperature(Altitude);
				Density = Body.GetDensity(Pressure, AtmosphericTemperature);
				DynamicPressure = 0.0005 * Density * Speed*Speed;

				var soundV = Body.GetSpeedOfSound(Pressure, Density);
				var mach = soundV > 0? Speed/soundV : 0;
				var convectiveMachLerp = Math.Pow(UtilMath.Clamp01((mach - PhysicsGlobals.NewtonianMachTempLerpStartMach) / 
				                                                   (PhysicsGlobals.NewtonianMachTempLerpEndMach - PhysicsGlobals.NewtonianMachTempLerpStartMach)), 
				                                      PhysicsGlobals.NewtonianMachTempLerpExponent);
				ShockTemperature = Speed * PhysicsGlobals.NewtonianTemperatureFactor;
				if (convectiveMachLerp > 0.0)
				{
					double b = PhysicsGlobals.MachTemperatureScalar * Math.Pow(Speed, PhysicsGlobals.MachTemperatureVelocityExponent);
					ShockTemperature = UtilMath.LerpUnclamped(ShockTemperature, b, convectiveMachLerp);
				}
				ShockTemperature *= (double)HighLogic.CurrentGame.Parameters.Difficulty.ReentryHeatScale * Body.shockTemperatureMultiplier;
				ShockTemperature = Math.Max(AtmosphericTemperature, ShockTemperature);
				//calculate convective coefficient for speed > Mach1; lower speed is not a consern
				ConvectiveCoefficient = 1E-10 * PhysicsGlobals.MachConvectionFactor;
				ConvectiveCoefficient *= Density > 1? Density : Math.Pow(Density, PhysicsGlobals.MachConvectionDensityExponent);
				ConvectiveCoefficient *= Math.Pow(Speed, PhysicsGlobals.MachConvectionVelocityExponent) * Body.convectionMultiplier;
			}
		}

		public override string ToString()
		{
			return Utils.Format("Altitude {} m, Density {}, Pressure {} kPa, Atm.T {} K\n" +
			                    "Speed {} m/s, Dyn.Pressure {} kPa, Shock.T {} K\n" +
			                    "Duration {} s, ConvectiveCoefficient {}",
			                    Altitude, Density, Pressure, AtmosphericTemperature, 
			                    Speed, DynamicPressure, ShockTemperature, 
			                    Duration, ConvectiveCoefficient);
		}
	}
}

