﻿/***************************************************************************
 * 
 * QuMESHS (Quantum Mesoscopic Electronic Semiconductor Heterostructure
 * Solver) for calculating electron and hole densities and electrostatic
 * potentials using self-consistent Poisson-Schroedinger solutions in 
 * layered semiconductors
 * 
 * Copyright(C) 2015 E. T. Owen and C. H. W. Barnes
 * 
 * The MIT License (MIT)
 * 
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction, including without limitation the rights
 * to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
 * copies of the Software, and to permit persons to whom the Software is
 * furnished to do so, subject to the following conditions:
 * 
 * The above copyright notice and this permission notice shall be included in all
 * copies or substantial portions of the Software.
 * 
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 * IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 * FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 * AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 * LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
 * OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
 * SOFTWARE.
 * 
 * For additional information, please contact eto24@cam.ac.uk or visit
 * <http://www.qumeshs.org>
 * 
 **************************************************************************/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using CenterSpace.NMath.Core;
using CenterSpace.NMath.Matrix;
using Solver_Bases;
using System.Threading;
using Solver_Bases.Layers;
using Solver_Bases.Geometry;

namespace OneD_ThomasFermiPoisson
{
    public class OneD_PoissonSolver : Potential_Base
    {
        Experiment exp;

        double top_bc, bottom_bc;
        double top_eps, bottom_eps;

        // parameters for regular grid solve
        DoubleTriDiagMatrix laplacian;
        DoubleTriDiagFact lu_fact;

        string dens_filename = "dens_1D.dat";

        public OneD_PoissonSolver(Experiment exp, bool using_external_code, Dictionary<string, object> input)
            : base(using_external_code)
        {
            this.exp = exp;
            
            // generate Laplacian matrix (spin-resolved)
            if (!using_external_code)
            {
                laplacian = Generate_Laplacian(exp.Layers);
                lu_fact = new DoubleTriDiagFact(laplacian);
            }
        }

        protected override Band_Data Parse_Potential(string location, string[] data)
        {
            return Band_Data.Parse_Band_Data(location, data, exp.Nz_Dens);
        }

        protected override Band_Data Get_Pot_On_Regular_Grid(Band_Data charge_density)
        {
            // set the top and bottom boundary conditions where [0] is the bottom of the device
            charge_density.vec[0] = bottom_bc * -1.0 * bottom_eps / (exp.Dz_Pot * exp.Dz_Pot);
            charge_density.vec[charge_density.Length - 1] = top_bc * -1.0 * top_eps / (exp.Dz_Pot * exp.Dz_Pot);

            // solve Poisson's equation
            Band_Data potential = new Band_Data(lu_fact.Solve(-1.0 * charge_density.vec));
            // and save its laplacian
            potential.Laplacian = Calculate_Phi_Laplacian(potential);

            return potential;
        }

 //       protected override Band_Data Get_ChemPot_From_External(Band_Data density)
 //       {
 //           Save_Data(density, dens_filename);
 //
 //           return Get_Data_From_External(flexpde_script, initcalc_result_filename);
 //       }

        /// <summary>
        /// Calculates the Laplacian for the given band structure of the input potential
        /// ie. returns d(eps * d(input))
        /// NOTE: the input should be a potential so make sure you divide all band energies by q_e
        /// </summary>
        public Band_Data Calculate_Phi_Laplacian(Band_Data input)
        {
            DoubleTriDiagMatrix lap_mat = Generate_Laplacian(exp.Layers);
            return new Band_Data(MatrixFunctions.Product(lap_mat, input.vec));
        }

        public override Band_Data Calculate_Newton_Step(SpinResolved_Data rho_prime, Band_Data g_phi)
        {
            DoubleTriDiagMatrix lap_mat = -1.0 * Generate_Laplacian(exp.Layers);
            Band_Data rho_prime_spin_summed = rho_prime.Spin_Summed_Data;

            // check that lap_mat and rho_prime have the same dimension
            if (rho_prime_spin_summed.Length != lap_mat.Rows)
                throw new Exception("Error - the Laplacian generated by pois_solv has a different order to rho_prime!");

            for (int i = 0; i < rho_prime_spin_summed.Length; i++)
                lap_mat[i, i] -= rho_prime_spin_summed.vec[i];

            DoubleTriDiagFact lu_newt_step = new DoubleTriDiagFact(lap_mat);

            // calculate newton step and its laplacian
            Band_Data result = new Band_Data(lu_newt_step.Solve(-1.0 * g_phi.vec));
            result.Laplacian = Calculate_Phi_Laplacian(result);

            return result;
        }

        /// <summary>
        /// Generates a laplacian matrix in one-dimension on a regular grid with Dirichlet BCs wot varying permitivity
        /// </summary>
        DoubleTriDiagMatrix Generate_Laplacian(ILayer[] layers)
        {
            DoubleTriDiagMatrix result = new DoubleTriDiagMatrix(exp.Nz_Pot, exp.Nz_Pot);
            double factor_plus, factor_minus;

            // cycle through the structure and fill in the Laplacian with the correct permittivities
            for (int i = 1; i < exp.Nz_Pot - 1; i++)
            {
                double eps_plus = Geom_Tool.GetLayer(layers, i * exp.Dz_Pot + 0.5 * exp.Dz_Pot + exp.Zmin_Pot).Permitivity;
                double eps_minus = Geom_Tool.GetLayer(layers, i * exp.Dz_Pot - 0.5 * exp.Dz_Pot + exp.Zmin_Pot).Permitivity;

                // the factor which multiplies the Laplace equation
                factor_plus = eps_plus / (exp.Dz_Pot * exp.Dz_Pot);
                factor_minus = eps_minus / (exp.Dz_Pot * exp.Dz_Pot);

                // on-diagonal term
                result[i, i] = -1.0 * factor_minus + -1.0 * factor_plus;
                // off-diagonal
                result[i, i - 1] = 1.0 * factor_minus;
                result[i, i + 1] = 1.0 * factor_plus;
            }

            // and fix boundary conditions
            double factor = Geom_Tool.GetLayer(layers, exp.Zmin_Pot).Permitivity / (exp.Dz_Pot * exp.Dz_Pot);
            result[0, 0] = 1.0 * factor;
            result[0, 1] = 0.0;
            factor = Geom_Tool.GetLayer(layers, (exp.Nz_Pot - 1) * exp.Dz_Pot + exp.Zmin_Pot).Permitivity / (exp.Dz_Pot * exp.Dz_Pot);
            result[exp.Nz_Pot - 1, exp.Nz_Pot - 1] = 1.0 * factor;
            result[exp.Nz_Pot - 1, exp.Nz_Pot - 2] = 0.0;

            return result;
        }

        public double Get_Surface_Charge(Band_Data chem_pot, ILayer[] layers)
        {
            // calculate the electric field just below the surface
            int surface = (int)(-1.0 * Math.Floor(Geom_Tool.Get_Zmin(layers) / exp.Dz_Pot));
            double eps = Geom_Tool.Find_Layer_Below_Surface(layers).Permitivity;
            // by Gauss' theorem, rho = - epsilon_0 * epsilon_r * dV/dz
            double surface_charge = -1.0 * eps * (chem_pot[surface-1] - chem_pot[surface - 2]) / exp.Dz_Pot;
            // divide by - q_e to convert the chemical potential into a potential
            surface_charge /= -1.0 * Physics_Base.q_e;

            return surface_charge;
        }

        /*
        /// <summary>
        /// creates an input file for flexPDE to solve a 1D poisson equation
        /// </summary>
        protected override void Create_FlexPDE_Input_File(string flexPDE_input, string dens_filename, double[] layer_depths)
        {
            string well_dens_filename = dens_filename;
            string dopent_dens_filename = dens_filename;

            double dopent_depth = layer_depths[1];
            double dopent_width = layer_depths[2] - layer_depths[1];
            double well_depth = layer_depths[layer_depths.Length - 2];           // put the top of the heterostructure boundary as the penultimate layer boundary

            // check if the dopents should be frozen out
            if (freeze_out_dopents)
                // if true, change the input density filename for the dopents
                dopent_dens_filename = "dopents_frozen_" + dens_filename;
            
            // check if an input file already exists and delete it
            if (File.Exists(flexPDE_input))
                File.Delete(flexPDE_input);

            // open up a new streamwriter to create the input file
            StreamWriter sw = new StreamWriter(flexPDE_input);

            // write the file
            sw.WriteLine("TITLE \'Band Structure\'");
            sw.WriteLine("COORDINATES cartesian1");
            sw.WriteLine("VARIABLES");
            sw.WriteLine("\tu");
            sw.WriteLine("SELECT");
            // gives the flexPDE tolerance for the finite element solve
            sw.WriteLine("\tERRLIM=1e-5");
            sw.WriteLine("DEFINITIONS");
            // this is where the density variable
            sw.WriteLine("\trho\t! density");
            // number of lattice sites that the density needs to be output to
            sw.WriteLine("\tnx = " + nz.ToString());
            // size of the sample
            sw.WriteLine("\tlx = " + (nz * dz).ToString());
            // the top boundary condition on the surface of the sample
            sw.WriteLine("\t! Boundary conditions");
            sw.WriteLine("\ttop_V = " + top_bc.ToString());
            sw.WriteLine("\tbottom_V = " + bottom_bc.ToString());
            sw.WriteLine();
            sw.WriteLine("\t! Electrical permitivities");
            sw.WriteLine("\teps_0 = " + Physics_Base.epsilon_0.ToString());
            // relative permitivity of GaAs
            sw.WriteLine("\teps_r = " + Physics_Base.epsilon_r.ToString());
            sw.WriteLine("\teps");
            sw.WriteLine();
            // boundary layer definitions (note that this is quite a specific layer structure)
            sw.WriteLine("\t! boundary layer definitions");
	        sw.WriteLine("\tdopent_top = " + dopent_depth.ToString());
	        sw.WriteLine("\tdopent_bottom = " + (dopent_depth + dopent_width).ToString());
            sw.WriteLine("\twell_top = " + well_depth.ToString());           // put the top of the heterostructure boundary as the penultimate layer boundary
            sw.WriteLine("\twell_bottom = lx");
            sw.WriteLine(); 
            sw.WriteLine("EQUATIONS");
            // Poisson's equation
            sw.WriteLine("\tu: div(eps * grad(u)) = -rho\t! Poisson's equation");
            sw.WriteLine();
            // the boundary definitions for the differnet layers
            sw.WriteLine("BOUNDARIES");
            sw.WriteLine("\tREGION 1	! capping layer");
            sw.WriteLine("\t\trho = 0");
            sw.WriteLine("\t\teps = eps_0 * eps_r");
            sw.WriteLine("\t\tSTART(0)");
            sw.WriteLine("\t\tPOINT VALUE(u) = top_V");
            sw.WriteLine("\t\tLINE TO (dopent_top)");
            sw.WriteLine("\tREGION 2	! dopent layer");
            sw.WriteLine("\t\trho = TABLE(\'" + dopent_dens_filename + "\', x)");
            sw.WriteLine("\t\teps = eps_0 * eps_r");
            sw.WriteLine("\t\tSTART(dopent_top)");
            sw.WriteLine("\t\tLINE TO (dopent_bottom)");
            sw.WriteLine("\tREGION 3	! separator layer");
            sw.WriteLine("\t\trho = 0");
            sw.WriteLine("\t\teps = eps_0 * eps_r");
            sw.WriteLine("\t\tSTART(dopent_bottom)");
            sw.WriteLine("\t\tLINE TO (well_top)");
            sw.WriteLine("\tREGION 3	! well layer");
            sw.WriteLine("\t\trho = TABLE(\'" + well_dens_filename + "\', x)");
            sw.WriteLine("\t\teps = eps_0 * eps_r");
            sw.WriteLine("\t\tSTART(well_top)");
            sw.WriteLine("\t\tLINE TO (well_bottom)");
            // a possible bottomr boundary condition which hasn't worked yet...
            // this form of the boundary condition is equivalent to " d(eps * u)/dn + (bottom_V - u) = 0.0 "
            // which gives a combined Neumann/Dirichlet 
            sw.WriteLine("\t\tPOINT VALUE(u) = bottom_V");
            //sw.WriteLine("\t\tPOINT NATURAL(u) = (bottom_V - u)");
            sw.WriteLine();
            sw.WriteLine("PLOTS");
            sw.WriteLine("\tELEVATION(rho) FROM (0) TO (lx)");
	        sw.WriteLine("\tELEVATION(u) FROM (0) TO (lx) EXPORT(nx) FORMAT \'#1\' FILE=\'pot.dat\'");
            sw.WriteLine("END");

            // and close the file writer
            sw.Close();
        }
        */

        public void Create_FlexPDE_File(double top_bc, double split_bc1, double split_bc2, double split_width, double surface, double bottom_bc, string output_file)
        {
            // check if an input file already exists and delete it
            if (File.Exists(output_file))
                File.Delete(output_file);

            // open up a new streamwriter to create the input file
            StreamWriter sw = new StreamWriter(output_file);

            // write the file
            sw.WriteLine("TITLE \'Band Structure\'");
            sw.WriteLine("COORDINATES cartesian1");
            sw.WriteLine("VARIABLES");
            sw.WriteLine("\tu");
            sw.WriteLine("SELECT");
            // gives the flexPDE tolerance for the finite element solve
            sw.WriteLine("\tERRLIM=1e-5");
            sw.WriteLine("DEFINITIONS");
            // this is where the density variable
            sw.WriteLine("\trho\t! density");
            sw.WriteLine("\tband_gap");
            // number of lattice sites that the density needs to be output to
            sw.WriteLine("\tnz = " + exp.Nz_Pot.ToString());
            // size of the sample
            sw.WriteLine("\tlz = " + (exp.Nz_Pot * exp.Dz_Pot).ToString());
            // the top boundary condition on the surface of the sample
            sw.WriteLine("\t! Boundary conditions (in meV zC^-1)");
            sw.WriteLine("\ttop_V = " + top_bc.ToString());
            sw.WriteLine("\tbottom_V = " + bottom_bc.ToString());
            sw.WriteLine();
            sw.WriteLine("\t! Electrical permitivity");
            sw.WriteLine("\teps");
            sw.WriteLine();
            // other physical parameters
            sw.WriteLine("\tq_e = " + Physics_Base.q_e.ToString() + " ! charge of electron in zC");
            sw.WriteLine();
            sw.WriteLine("EQUATIONS");
            // Poisson's equation
            sw.WriteLine("\tu: div(eps * grad(u)) = - rho\t! Poisson's equation");
            sw.WriteLine();
            // the boundary definitions for the differnet layers
            sw.WriteLine("BOUNDARIES");

            // cycle through layers
            for (int i = 1; i < exp.Layers.Length; i++)
            {
                sw.WriteLine("\tREGION " + exp.Layers[i].Layer_No.ToString());
                sw.WriteLine("\t\trho = TABLE(\'" + dens_filename + "\', x)");
                sw.WriteLine("\t\teps = " + exp.Layers[i].Permitivity.ToString());
                sw.WriteLine("\t\tband_gap = " + exp.Layers[i].Band_Gap.ToString());
                sw.WriteLine("\t\tSTART(" + exp.Layers[i].Zmin.ToString() + ")");
                if (i == 1)
                    sw.WriteLine("\t\tPOINT VALUE(u) = top_V");
                sw.WriteLine("\t\tLINE TO (" + exp.Layers[i].Zmax.ToString() + ")");
                if (i == exp.Layers.Length - 1)
                    sw.WriteLine("\t\tPOINT VALUE(u) = bottom_V");
                sw.WriteLine();
            }

            sw.WriteLine("PLOTS");
            sw.WriteLine("\tELEVATION(- q_e * u + 0.5 * band_gap) FROM (-lz) TO (0)");
            sw.WriteLine("\tELEVATION(rho) FROM (-lz) TO (0)");
            sw.WriteLine("\tELEVATION(u) FROM (" + exp.Zmin_Pot.ToString() + ") TO (" + (exp.Zmin_Pot + exp.Nz_Pot * exp.Dz_Pot).ToString() + ") EXPORT(nz) FORMAT \'#1\' FILE=\'pot.dat\'");
            sw.WriteLine("END");

            // and close the file writer
            sw.Close();
        }

        protected override void Save_Data(Band_Data density, string input_file_name)
        {
            density.Save_1D_Data(input_file_name, exp.Dz_Pot, exp.Zmin_Pot);
        }

        public override void Initiate_Poisson_Solver(Dictionary<string, double> device_dimensions, Dictionary<string, double> boundary_conditions)
        {
            // get permittivities at top and bottom of the domain
            top_eps = Geom_Tool.GetLayer(exp.Layers, device_dimensions["top_position"]).Permitivity;
            bottom_eps = Geom_Tool.GetLayer(exp.Layers, device_dimensions["bottom_position"]).Permitivity;

            this.top_bc = boundary_conditions["top_V"] * Physics_Base.energy_V_to_meVpzC;
            this.bottom_bc = boundary_conditions["bottom_V"] * Physics_Base.energy_V_to_meVpzC;

            Console.WriteLine("WARNING - If you are trying to use FlexPDE in the 1D solver, it will not work...");
        }

        public override Band_Data Chemical_Potential
        {
            get { throw new NotImplementedException(); }
        }

        protected override string[] Trim_Potential_File(string[] lines)
        {
            throw new NotImplementedException();
        }

        protected override Band_Data Get_Pot_From_External(Band_Data density)
        {
            throw new NotImplementedException();
        }

        public override Band_Data Calculate_Newton_Step(SpinResolved_Data rho_prime, Band_Data gphi, SpinResolved_Data carrier_density, Band_Data dft_pot, Band_Data dft_calc)
        {
            throw new NotImplementedException();
        }
    }
}