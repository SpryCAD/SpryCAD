# Units: values that keep their size

## The problem

A part's units (MMGS, IPS) do two jobs today: they say how values are shown, and they say what a number without a
unit means when it is worked out from variables. Equations are mostly written without units (`OD = 150`), and a
dimension or feature value built from them (`OD`, `PCD/2`, `T + C`) is read in the part's units. Switching a part to
inches therefore turns `OD = 150` into 150 inches: the flange grows 25 times while values written with a unit (a
library hole's `13.4938mm`, a fillet's `10mm`) keep their size. And an inch part still shows the millimeter values
it was given in millimeters.

What a part's units should do is what they do in Inventor and (for dimensions) SolidWorks: say how values are shown
and how a number typed without a unit is read. Never what size the part is.

## The model

1. **Values keep their units.** A value typed without a unit gets the part's unit as it is typed (page fields
   already do this: `10` is kept as `10in` in an inch part, `W/2` as `(W/2)in`). Equations and their formulas get
   units the same way (`OD = 150mm`, `PCD = OD - 32mm`); counts and ratios stay plain numbers (`N = 8`).
2. **The units unitless values were written in are the part's own** (`CadDocument.ValueUnits`): set from the
   part's units when it is first saved with them or read from a file written before, and not changed by changing the
   part's units. Anything still written without a unit is read in them. So changing units never changes geometry,
   whether or not a part's values have been given units yet.
3. **The part's units are for showing and typing.** Fields show a value that is a plain number with a unit
   (`150mm`) in the part's units (`5.9055in`), keeping the exact value until it is edited; a formula is shown as
   written, what it works out to in the part's units. The Parameters window shows each variable's value in the
   part's units and its kind. Sketch dimensions show their values in the part's units.

## Which values are lengths, which are counts

Every place a value is used says what kind it needs: a sketch dimension or a depth reads a length, a draft angle an
angle, a pattern's count a plain number. A rebuild records each value it reads and the kind it read it as. From
those uses, and the equations and design tables that set variables, each variable's kind is worked out as
dimensions are in physics (`UnitInference`):

- each expression is parsed; a variable's power of length is an unknown, a number with a unit a known power, a
  number without one free to be whatever its place needs;
- multiplying adds powers, dividing subtracts them, `x^2` doubles, `sqrt` halves; adding, subtracting, comparing,
  `min`, `max` and `if`'s two values need the same power; trig functions take plain numbers (or angles) and give
  plain numbers; `(expr)in` needs `expr` plain;
- a use as a length needs power 1, as an angle or a count power 0; an equation `V = expr` gives `V` the power of
  `expr`;
- the equations are solved together (Gaussian elimination). A variable whose power comes out 1 is a length, 0 a plain
  number (an angle where it is read as one); one nothing constrains has no kind. Uses that contradict the others
  (a length in one place, a count in another) are set aside and their variables flagged.

So `D1 = OD` makes OD a length, a count of `N` makes N a number, `PCD*sin(180/N)` used as a length makes PCD a length
and N a number, and `PCD = OD - 32` makes the 32 a length too.

## Giving a part's values units

`UnitInference.AddUnits` writes each length's unit into the part's values, from the inference: a plain-number
equation of a length variable gets the part's value units (`150` → `150mm`), and every number without a unit that
the inference found to be a length, in an equation, a design table cell, a feature value or a sketch dimension
worked out from variables, gets them too (`OD - 32` → `OD - 32mm`). A value that is a number alone in a feature or a
dimension is left as it is (millimeters, as files keep them).

It is checked before it is kept: every value the rebuild read is worked out again from the new text, and if any
differs (or any expression could not be parsed), nothing is changed. It runs when a part from before this is opened
(its message says how many values were given units) and from the Parameters window (Add Units). A part it could not
convert still builds the same: its unitless values are read in its value units.

## Assemblies

An assembly's units only ever said how values are shown: its mates keep their values as numbers (millimeters,
degrees), converted when typed, and it has no variables. Its Configure dialog shows a part's length parameters in
the assembly's units and reads a number typed alone in them ("6" in an inch assembly is 6in), the value kept
exactly while it is shown unchanged.

## Not in this change

- A display precision per part (the units' own decimals are used).
