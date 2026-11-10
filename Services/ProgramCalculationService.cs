using ProgramInsights.Models;

namespace ProgramInsights.Services;

public class ProgramCalculationService
{
    public ProgramCalculationResultDto Calculate(ProgramDetailsDto program)
    {
        Validate(program);

        var result = new ProgramCalculationResultDto
        {
            ProgramName = program.Name,
            StartYear = program.StartYear
        };

        for (var forecastYearIndex = 0; forecastYearIndex < program.Settings.Count; forecastYearIndex++)
        {
            CalculateYear(program, forecastYearIndex, result);
        }

        result.BreakevenResults = CalculateBreakeven(program, result.CostResults);

        return result;
    }

    private static void CalculateYear(ProgramDetailsDto program, int forecastYearIndex, ProgramCalculationResultDto result)
    {
        var forecastSetting = program.Settings[forecastYearIndex];
        var totalFacultyCost = 0d;
        var totalFacultyRevenue = 0d;
        var totalNonFacultyCost = 0d;
        var totalEftsl = 0d;

        // Group units by study year so each cohort is matched to the right units.
        var unitsByStudyYear = program.Units
            .Where(u => !string.IsNullOrWhiteSpace(u.Unit) && !string.IsNullOrWhiteSpace(u.Profile))
            .GroupBy(u => u.ProgramYear)
            .ToDictionary(g => g.Key, g => g.ToList());

        // Each settings row whose ForecastYearIndex <= current year is an active cohort.
        // cohortStartIndex 0 → first-year students in forecast year 0,
        //                      second-year students in forecast year 1, etc.
        for (var cohortStartIndex = 0; cohortStartIndex <= forecastYearIndex; cohortStartIndex++)
        {
            var cohortSetting = program.Settings[cohortStartIndex];
            var studyYear = forecastYearIndex - cohortStartIndex + 1;

            if (!unitsByStudyYear.TryGetValue(studyYear, out var unitsForStudyYear))
                continue;

            var cohortDom = cohortSetting.DomesticEftsl;
            var cohortInt = cohortSetting.InternationalEftsl;
            var cohortCgs = cohortSetting.CgsEftsl;
            var cohortTotal = cohortDom + cohortInt + cohortCgs;

            totalEftsl += cohortTotal;

            // Revenue and NFC are program-level — earned once per EFTSL regardless of
            // how many core units the student takes. Use program defaults for these.
            var programValues = ResolveProgramDefaults(program);
            var isFoeCluster = program.RevCgsSelect == "Cluster";
            var revMultiplier = AdjustForInflation(program.Settings, forecastYearIndex, expense: false);

            // When FOE cluster mode, CGS revenue is attributed per unit from each unit's FOE rate.
            // Dom+Int revenue is still cohort-level and split evenly across units for display.
            var domIntRevenue = AdjustForInflation(
                programValues.DomesticRevenuePerEftsl * cohortDom +
                programValues.InternationalRevenuePerEftsl * cohortInt,
                program.Settings, forecastYearIndex, expense: false);
            var cohortCgsRevenue = isFoeCluster ? 0 : AdjustForInflation(
                programValues.CgsRevenuePerEftsl * cohortCgs,
                program.Settings, forecastYearIndex, expense: false);
            var cohortRevenue = domIntRevenue + cohortCgsRevenue;

            var cohortNfc = AdjustForInflation(
                programValues.NonFacultyCostPerEftsl * cohortTotal,
                program.Settings, forecastYearIndex, expense: true);

            totalFacultyRevenue += cohortRevenue;
            totalNonFacultyCost += cohortNfc;

            var unitCount = unitsForStudyYear.Count;

            foreach (var unit in unitsForStudyYear)
            {
                var values = ResolveUnitValues(program, unit);
                var profile = ResolveProfile(program, unit);
                if (profile is null) continue;

                // All units are core — every student attends every unit.
                var isNew = !program.UnitValues.Any(v => v.UnitName == unit.Unit);

                var hours = CalculateHours(
                    program, unit, profile,
                    forecastYearIndex, cohortStartIndex, studyYear,
                    cohortDom, cohortInt, cohortCgs, isNew);
                result.HourResults.Add(hours);

                // Faculty cost scales with teaching hours (full cohort class size per unit).
                var unitFacCost = AdjustForInflation(hours.TotalHours * values.FacultyCostPerHour, program.Settings, forecastYearIndex, expense: true);

                // CGS revenue per unit from FOE code; Dom+Int split evenly across units.
                var unitCgsRev = 0d;
                if (isFoeCluster && unitCount > 0)
                {
                    var foeRate = ResolveFoeRate(program, unit.FoeCodeDetailed);
                    unitCgsRev = foeRate * (cohortCgs / unitCount) * revMultiplier;
                    totalFacultyRevenue += unitCgsRev;
                }

                var unitRevShare = (unitCount == 0 ? 0 : cohortRevenue / unitCount) + unitCgsRev;
                var unitNfcShare = unitCount == 0 ? 0 : cohortNfc / unitCount;

                hours.FacultyCost = unitFacCost;
                hours.NonFacultyCost = unitNfcShare;
                hours.FacultyRevenue = unitRevShare;
                hours.TotalCost = unitFacCost + unitNfcShare;
                hours.Margin = unitRevShare - hours.TotalCost;
                hours.MarginPercent = hours.TotalCost == 0 ? 0 : unitRevShare / hours.TotalCost - 1;

                totalFacultyCost += unitFacCost;
            }
        }

        // Fixed expense is added once per forecast year, not per unit.
        totalNonFacultyCost += AdjustForInflation(
            forecastSetting.FixedExpense,
            program.Settings, forecastYearIndex, expense: true);

        var totalHours = result.HourResults
            .Where(h => h.ForecastYearIndex == forecastYearIndex)
            .Sum(h => h.TotalHours);

        result.CostResults.Add(CreateCostResult(
            program, forecastYearIndex,
            totalFacultyCost, totalFacultyRevenue, totalNonFacultyCost,
            totalEftsl, totalHours));
    }

    private static ProgramProfileDto? ResolveProfile(ProgramDetailsDto program, ProgramUnitDto unit)
    {
        if (unit.Profile == "Custom" && unit.CustomTutSize.HasValue)
        {
            return new ProgramProfileDto
            {
                Name = "Custom",
                TutSize = unit.CustomTutSize ?? 20,
                LectureHours = unit.CustomLectHours ?? 0,
                TutorialHours = unit.CustomTutHours ?? 0,
                PreparationHours = unit.CustomPrepHours ?? 0,
                AdministrationHours = unit.CustomAdminHours ?? 0,
                StudentHours = unit.CustomStudentHours ?? 0
            };
        }
        return program.Profiles.FirstOrDefault(p => p.Name == unit.Profile);
    }

    private static HourCalculationDto CalculateHours(
        ProgramDetailsDto program,
        ProgramUnitDto unit,
        ProgramProfileDto profile,
        int forecastYearIndex,
        int cohortStartYearIndex,
        int studyYear,
        double cohortDomEftsl,
        double cohortIntEftsl,
        double cohortCgsEftsl,
        bool isNew)
    {
        var cohortEftsl = cohortDomEftsl + cohortIntEftsl + cohortCgsEftsl;
        var studentsPerUnit = cohortEftsl * 8;
        var isNewUnit = isNew;

        var tutorialSize = Math.Max(profile.TutSize, 1);
        var unitHours = ((profile.LectureHours + profile.TutorialHours) * (1 + profile.PreparationHours)) + profile.AdministrationHours;
        var newAdditionalClasses = Math.Ceiling((studentsPerUnit - Math.Min(tutorialSize, studentsPerUnit)) / tutorialSize);
        var newRepeatTutorialHours = newAdditionalClasses * profile.TutorialHours * (1 + profile.PreparationHours);
        var currentAdditionalClasses = Math.Ceiling(studentsPerUnit / tutorialSize);
        var currentAdditionalTutorialHours = currentAdditionalClasses * profile.TutorialHours * (1 + profile.PreparationHours);
        var studentHours = profile.StudentHours * cohortEftsl * 8;
        var totalHours = (isNewUnit ? unitHours + newRepeatTutorialHours : currentAdditionalTutorialHours) + studentHours;

        return new HourCalculationDto
        {
            ForecastYearIndex = forecastYearIndex,
            CalendarYear = program.StartYear + forecastYearIndex,
            CohortStartYearIndex = cohortStartYearIndex,
            StudyYear = studyYear,
            Unit = unit.Unit,
            Profile = unit.Profile,
            DomesticEftsl = cohortDomEftsl,
            InternationalEftsl = cohortIntEftsl,
            CgsEftsl = cohortCgsEftsl,
            StudentsPerUnit = studentsPerUnit,
            NewUnitHours = isNewUnit ? unitHours : 0,
            StudentHours = studentHours,
            NewUnitAdditionalClasses = isNewUnit ? newAdditionalClasses : 0,
            NewUnitRepeatTutorialHours = isNewUnit ? newRepeatTutorialHours : 0,
            CurrentUnitAdditionalClasses = isNewUnit ? 0 : currentAdditionalClasses,
            CurrentUnitAdditionalTutorialHours = isNewUnit ? 0 : currentAdditionalTutorialHours,
            TotalHours = totalHours
        };
    }

    private static CostCalculationDto CreateCostResult(
        ProgramDetailsDto program,
        int yearIndex,
        double facultyCost,
        double facultyRevenue,
        double nonFacultyCost,
        double totalEftsl,
        double totalTeachingHours)
    {
        var totalCost = facultyCost + nonFacultyCost;
        var totalMargin = facultyRevenue - totalCost;

        return new CostCalculationDto
        {
            YearIndex = yearIndex,
            CalendarYear = program.StartYear + yearIndex,
            FacultyCost = facultyCost,
            FacultyRevenue = facultyRevenue,
            FacultyMargin = facultyRevenue - facultyCost,
            FacultyMarginPercent = facultyCost == 0 ? 0 : facultyRevenue / facultyCost - 1,
            NonFacultyCost = nonFacultyCost,
            TotalMargin = totalMargin,
            TotalMarginPercent = totalCost == 0 ? 0 : facultyRevenue / totalCost - 1,
            TotalEftsl = totalEftsl,
            TotalTeachingHours = totalTeachingHours
        };
    }

    private static ResolvedUnitValues ResolveUnitValues(ProgramDetailsDto program, ProgramUnitDto unit)
    {
        var programDefaults = ResolveProgramDefaults(program);
        if (unit.Unit == "New Unit")
        {
            return programDefaults;
        }

        var unitValue = program.UnitValues.FirstOrDefault(v => v.UnitName == unit.Unit);
        if (unitValue is null)
        {
            return programDefaults;
        }

        return new ResolvedUnitValues
        {
            FacultyCostPerHour = unitValue.FacultyCostPerHour == 0 ? programDefaults.FacultyCostPerHour : unitValue.FacultyCostPerHour,
            NonFacultyCostPerEftsl = unitValue.NonFacultyCostPerEftsl == 0 ? programDefaults.NonFacultyCostPerEftsl : unitValue.NonFacultyCostPerEftsl,
            DomesticRevenuePerEftsl = unitValue.DomesticRevenuePerEftsl == 0 ? programDefaults.DomesticRevenuePerEftsl : unitValue.DomesticRevenuePerEftsl,
            InternationalRevenuePerEftsl = unitValue.InternationalRevenuePerEftsl == 0 ? programDefaults.InternationalRevenuePerEftsl : unitValue.InternationalRevenuePerEftsl,
            CgsRevenuePerEftsl = unitValue.CgsRevenuePerEftsl == 0 ? programDefaults.CgsRevenuePerEftsl : unitValue.CgsRevenuePerEftsl
        };
    }

    private static ResolvedUnitValues ResolveProgramDefaults(ProgramDetailsDto program)
    {
        var summary = GetSelectedModelSummary(program);
        return new ResolvedUnitValues
        {
            FacultyCostPerHour = program.CphSelect switch
            {
                "Model" => summary.CostPerHour,
                "Benchmark" => program.BmCphValue,
                _ => program.CphCustom
            },
            NonFacultyCostPerEftsl = program.NfcSelect switch
            {
                "Model" => summary.NonCostPerEftsl,
                "Benchmark" => program.BmNfcValue,
                _ => program.NfcCustom
            },
            DomesticRevenuePerEftsl = program.RevDomSelect == "Model" ? summary.RevDomPerEftsl : program.RevDomCustom,
            InternationalRevenuePerEftsl = program.RevIntSelect == "Model" ? summary.RevIntPerEftsl : program.RevIntCustom,
            CgsRevenuePerEftsl = program.RevCgsSelect switch
            {
                "Model" => summary.RevCgsPerEftsl,
                "Cluster" => 0, // resolved per-unit from FOE code in CalculateYear / CalculateBreakeven
                _ => program.RevCgsCustom
            }
        };
    }

    private static ModelValueSummaryDto GetSelectedModelSummary(ProgramDetailsDto program)
    {
        var values = program.ModelValues
            .Where(v => string.IsNullOrWhiteSpace(program.SchoolSelect) || v.School == program.SchoolSelect)
            .Where(v => string.IsNullOrWhiteSpace(program.CourseSelect) || v.CourseName == program.CourseSelect)
            .Where(v => string.IsNullOrWhiteSpace(program.CareerSelect) || v.Career == program.CareerSelect)
            .ToList();

        if (values.Count == 0)
        {
            return new ModelValueSummaryDto();
        }

        var facultyExpense = values.Sum(x => x.FacultyExpense);
        var nonFacultyExpense = values.Sum(x => x.NonFacultyExpense);
        var domEftsl = values.Sum(x => x.DomesticEftsl);
        var intEftsl = values.Sum(x => x.InternationalEftsl);
        var cgsEftsl = values.Sum(x => x.CgsEftsl);
        var totalEftslRaw = values.Sum(x => x.TotalEftsl);    // raw SUM(EFTSL) — correct NFC denominator
        var deliveryHours = values.Sum(x => x.DeliveryHours);

        return new ModelValueSummaryDto
        {
            CostPerHour = deliveryHours == 0 ? 0 : facultyExpense / deliveryHours,
            NonCostPerEftsl = totalEftslRaw == 0 ? 0 : nonFacultyExpense / totalEftslRaw,
            RevCgsPerEftsl = cgsEftsl == 0 ? 0 : values.Sum(x => x.CgsRevenue) / cgsEftsl,
            RevDomPerEftsl = domEftsl == 0 ? 0 : values.Sum(x => x.DomesticRevenue) / domEftsl,
            RevIntPerEftsl = intEftsl == 0 ? 0 : values.Sum(x => x.InternationalRevenue) / intEftsl,
            DeliveryHours = deliveryHours
        };
    }

    private static double AdjustForInflation(List<ProgramYearSettingDto> settings, int yearIndex, bool expense)
        => settings.Take(yearIndex + 1).Aggregate(1d, (acc, setting) => acc * (1 + ((expense ? setting.ExpenseCpi : setting.RevenueCpi) / 100)));

    private static double AdjustForInflation(double value, List<ProgramYearSettingDto> settings, int yearIndex, bool expense)
        => value * AdjustForInflation(settings, yearIndex, expense);

    /// <summary>
    /// Iterative simulation: walks EFTSL upward in small steps (matching the original AdamTool2
    /// approach), recalculating teaching hours at each step so that tutorial-group step-changes
    /// are captured. Returns chart points (EFTSL, Revenue, Cost) for each step plus the precise
    /// breakeven EFTSL where Revenue first >= Cost.
    /// </summary>
    private static List<BreakevenCalculationDto> CalculateBreakeven(
        ProgramDetailsDto program,
        List<CostCalculationDto> costResults)
    {
        var results = new List<BreakevenCalculationDto>();

        var unitsByStudyYear = program.Units
            .Where(u => !string.IsNullOrWhiteSpace(u.Unit) && !string.IsNullOrWhiteSpace(u.Profile))
            .GroupBy(u => u.ProgramYear)
            .ToDictionary(g => g.Key, g => g.ToList());

        var programValues = ResolveProgramDefaults(program);

        // For FOE cluster mode, compute the average CGS rate across all units as the effective rate for breakeven.
        if (program.RevCgsSelect == "Cluster")
        {
            var allUnits = unitsByStudyYear.Values.SelectMany(u => u).ToList();
            var avgFoeRate = allUnits.Count == 0 ? 0 :
                allUnits.Average(u => ResolveFoeRate(program, u.FoeCodeDetailed));
            programValues = new ResolvedUnitValues
            {
                FacultyCostPerHour = programValues.FacultyCostPerHour,
                NonFacultyCostPerEftsl = programValues.NonFacultyCostPerEftsl,
                DomesticRevenuePerEftsl = programValues.DomesticRevenuePerEftsl,
                InternationalRevenuePerEftsl = programValues.InternationalRevenuePerEftsl,
                CgsRevenuePerEftsl = avgFoeRate
            };
        }

        foreach (var costRow in costResults)
        {
            var i = costRow.YearIndex;

            // CPI multipliers for this forecast year
            var revMultiplier = AdjustForInflation(program.Settings, i, expense: false);
            var expMultiplier = AdjustForInflation(program.Settings, i, expense: true);
            var fixedCostInflated = program.Settings[i].FixedExpense * expMultiplier;

            // Collect all active cohorts for this forecast year and their reference EFTSL.
            var cohorts = new List<(int ci, double dom, double intl, double cgs)>();
            for (var ci = 0; ci <= i; ci++)
            {
                var s = program.Settings[ci];
                cohorts.Add((ci, s.DomesticEftsl, s.InternationalEftsl, s.CgsEftsl));
            }

            // Reference totals (the actual projected EFTSL for this year)
            var refDom  = cohorts.Sum(c => c.dom);
            var refInt  = cohorts.Sum(c => c.intl);
            var refCgs  = cohorts.Sum(c => c.cgs);
            var refTotal = refDom + refInt + refCgs;

            if (refTotal == 0)
            {
                results.Add(new BreakevenCalculationDto
                {
                    YearIndex = i,
                    CalendarYear = costRow.CalendarYear
                });
                continue;
            }

            // Analytical estimate for the breakeven so we can choose a sensible step/range.
            var totalCostRef = costRow.FacultyCost + costRow.NonFacultyCost;
            var varCostRef   = totalCostRef - fixedCostInflated;
            var revPerEftsl  = refTotal == 0 ? 0 : costRow.FacultyRevenue / refTotal;
            var varPerEftsl  = refTotal == 0 ? 0 : varCostRef / refTotal;
            var cm           = revPerEftsl - varPerEftsl;

            var estimatedBreakeven = cm > 0 ? fixedCostInflated / cm : refTotal * 3;
            var rangeMax = Math.Max(estimatedBreakeven * 2, refTotal * 1.5);
            var step     = Math.Max(0.125, rangeMax / 100d);

            // Add the origin point: at EFTSL = 0, revenue = 0, cost = fixed cost only.
            var chartPoints = new List<BreakevenChartPoint>
            {
                new() { Eftsl = 0, Revenue = 0, TotalCost = fixedCostInflated }
            };

            double? breakevenEftsl = null;

            for (var scaledTotal = step; scaledTotal <= rangeMax + step; scaledTotal += step)
            {
                var scale = scaledTotal / refTotal;

                // Revenue scales linearly with EFTSL (keeping Dom/Int/CGS mix constant).
                var revenue = (programValues.DomesticRevenuePerEftsl      * refDom  * scale +
                               programValues.InternationalRevenuePerEftsl * refInt  * scale +
                               programValues.CgsRevenuePerEftsl           * refCgs  * scale) * revMultiplier;

                // NFC scales linearly with total EFTSL.
                var nfc = programValues.NonFacultyCostPerEftsl * scaledTotal * expMultiplier;

                // Faculty cost is recalculated from teaching hours at the scaled EFTSL,
                // capturing discrete tutorial-group step-changes.
                var facultyCost = 0d;
                foreach (var (ci, dom, intl, cgs) in cohorts)
                {
                    var cohortTotal = (dom + intl + cgs) * scale;
                    var studyYear   = i - ci + 1;

                    if (!unitsByStudyYear.TryGetValue(studyYear, out var units)) continue;

                    foreach (var unit in units)
                    {
                        var profile = ResolveProfile(program, unit);
                        if (profile is null) continue;

                        var values = ResolveUnitValues(program, unit);
                        var isNew  = !program.UnitValues.Any(v => v.UnitName == unit.Unit);

                        facultyCost += CalculateTeachingHours(profile, cohortTotal, isNew)
                                       * values.FacultyCostPerHour
                                       * expMultiplier;
                    }
                }

                var totalCost = facultyCost + nfc + fixedCostInflated;

                chartPoints.Add(new BreakevenChartPoint
                {
                    Eftsl     = Math.Round(scaledTotal, 3),
                    Revenue   = revenue,
                    TotalCost = totalCost
                });

                if (breakevenEftsl is null && revenue >= totalCost)
                    breakevenEftsl = scaledTotal;
            }

            var surplus     = breakevenEftsl.HasValue ? refTotal - breakevenEftsl.Value : 0;
            var surplusPct  = breakevenEftsl.HasValue && breakevenEftsl.Value > 0
                              ? surplus / breakevenEftsl.Value : 0;

            results.Add(new BreakevenCalculationDto
            {
                YearIndex                  = i,
                CalendarYear               = costRow.CalendarYear,
                ProjectedEftsl             = refTotal,
                BreakevenEftsl             = breakevenEftsl ?? 0,
                EftslSurplus               = surplus,
                SurplusPercent             = surplusPct,
                FixedCost                  = fixedCostInflated,
                RevenuePerEftsl            = revPerEftsl,
                VariableCostPerEftsl       = varPerEftsl,
                ContributionMarginPerEftsl = cm,
                IsBreakevenPossible        = breakevenEftsl.HasValue,
                ChartPoints                = chartPoints
            });
        }

        return results;
    }

    // Lightweight hours-only calculation used by the breakeven simulation.
    // Mirrors the core maths in CalculateHours without allocating the full DTO.
    private static double CalculateTeachingHours(ProgramProfileDto profile, double cohortEftsl, bool isNew)
    {
        var students         = cohortEftsl * 8;
        var tutSize          = Math.Max(profile.TutSize, 1);
        var unitHours        = ((profile.LectureHours + profile.TutorialHours) * (1 + profile.PreparationHours)) + profile.AdministrationHours;
        var newExtraClasses  = Math.Ceiling((students - Math.Min(tutSize, students)) / tutSize);
        var newRepeatHours   = newExtraClasses * profile.TutorialHours * (1 + profile.PreparationHours);
        var curExtraClasses  = Math.Ceiling(students / tutSize);
        var curRepeatHours   = curExtraClasses * profile.TutorialHours * (1 + profile.PreparationHours);
        var studentHours     = profile.StudentHours * students;
        return (isNew ? unitHours + newRepeatHours : curRepeatHours) + studentHours;
    }

    // Matches a stored FOE code (any separator format, e.g. "010101 : Mathematics" or
    // "010101 - Mathematics") to a FoeCodeOption by comparing the leading 6 numeric digits.
    private static double ResolveFoeRate(ProgramDetailsDto program, string rawFoeCode)
    {
        if (string.IsNullOrWhiteSpace(rawFoeCode) || program.FoeCodeOptions.Count == 0) return 0;
        var prefix = new string(rawFoeCode.TakeWhile(char.IsDigit).ToArray());
        if (prefix.Length < 6) return 0;
        prefix = prefix[..6];
        return program.FoeCodeOptions
            .FirstOrDefault(f => f.FoeCodeDetailed.StartsWith(prefix))?.CgsRatePerEftsl ?? 0;
    }

    private static void Validate(ProgramDetailsDto program)
    {
        if (program.Settings.Count == 0)
            throw new InvalidOperationException("Add at least one projection year before calculating.");
        if (program.Units.Count == 0)
            throw new InvalidOperationException("Add at least one unit before calculating.");
        if (program.Settings.Any(s => s.DomesticEftsl + s.InternationalEftsl + s.CgsEftsl <= 0))
            throw new InvalidOperationException("Each projection year needs a positive commencing EFTSL total.");
    }

    private sealed class ResolvedUnitValues
    {
        public double FacultyCostPerHour { get; set; }
        public double NonFacultyCostPerEftsl { get; set; }
        public double DomesticRevenuePerEftsl { get; set; }
        public double InternationalRevenuePerEftsl { get; set; }
        public double CgsRevenuePerEftsl { get; set; }
    }
}
