﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Solver_Bases
{
    public enum Spin
    {
        Up = 0,
        Down = 1
    }

    public enum Material
    {
        GaAs,
        Al03GaAs,
        AlGaAs,
        AlAs,
        In075GaAs,
        InAs,
        PMMA,
        Air,
        Substrate
    }

    public enum Dopent
    {
        donor,
        acceptor
    }

    public enum Geometry_Type
    {
        Slab
    }
}
