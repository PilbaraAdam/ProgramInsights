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

            foreach (var unit in unitsForStudyYear)
            {
                var values = ResolveUnitValues(program, unit);
                var profile = ResolveProfile(program, unit);
                if (profile is null) continue;

                // A unit is "new" (needs full prep hours) if it has no historical data.
                var isNew = !program.UnitValues.Any(v => v.UnitName == unit.Unit);

                var hours = CalculateHours(
                    program, unit, profile,
                    forecastYearIndex, cohortStartIndex, studyYear,
                    cohortDom, cohortInt, cohortCgs,
                    unitsForStudyYear.Count, isNew);
                result.HourResults.Add(hours);

                var unitFacCost = AdjustForInflation(hours.TotalHours * values.FacultyCostPerHour, program.Settings, forecastYearIndex, expense: true);
                var unitNfc = AdjustForInflation(values.NonFacultyCostPerEftsl * (hours.DomesticEftsl + hours.InternationalEftsl + hours.CgsEftsl), program.Settings, forecastYearIndex, expense: true);
                var unitRevenue = AdjustForInflation(
                    values.DomesticRevenuePerEftsl * hours.DomesticEftsl +
                    values.InternationalRevenuePerEftsl * hours.InternationalEftsl +
                    values.CgsRevenuePerEftsl * hours.CgsEftsl,
                    program.Settings, forecastYearIndex, expense: false);

                hours.FacultyCost = unitFacCost;
                hours.NonFacultyCost = unitNfc;
                hours.FacultyRevenue = unitRevenue;
                hours.TotalCost = unitFacCost + unitNfc;
                hours.Margin = unitRevenue - hours.TotalCost;
                hours.MarginPercent = hours.TotalCost == 0 ? 0 : unitRevenue / hours.TotalCost - 1;

                totalFacultyCost += unitFacCost;
                totalFacultyRevenue += unitRevenue;
                totalNonFacultyCost += unitNfc;
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
        int unitsInStudyYear,
        bool isNew)
    {
        var cohortEftsl = cohortDomEftsl + cohortIntEftsl + cohortCgsEftsl;
        var eftslPerUnit = unitsInStudyYear == 0 ? 0 : cohortEftsl / unitsInStudyYear;
        var studentsPerUnit = eftslPerUnit * 8;
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
            DomesticEftsl = cohortDomEftsl / (unitsInStudyYear == 0 ? 1 : unitsInStudyYear),
            InternationalEftsl = cohortIntEftsl / (unitsInStudyYear == 0 ? 1 : unitsInStudyYear),
            CgsEftsl = cohortCgsEftsl / (unitsInStudyYear == 0 ? 1 : unitsInStudyYear),
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
            FacultyCostPerHour = program.CphSelect == "Model" ? summary.CostPerHour : program.CphCustom,
            NonFacultyCostPerEftsl = program.NfcSelect == "Model" ? summary.NonCostPerEftsl : program.NfcCustom,
            DomesticRevenuePerEftsl = program.RevDomSelect == "Model" ? summary.RevDomPerEftsl : program.RevDomCustom,
            InternationalRevenuePerEftsl = program.RevIntSelect == "Model" ? summary.RevIntPerEftsl : program.RevIntCustom,
            CgsRevenuePerEftsl = program.RevCgsSelect switch
            {
                "Model" => summary.RevCgsPerEftsl,
                "Cluster" => program.CgsClusters.FirstOrDefault(c => c.Name == program.RevCgsCluster)?.Value ?? program.RevCgsCustom,
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
        var totalEftsl = domEftsl + intEftsl + cgsEftsl;
        var deliveryHours = values.Sum(x => x.DeliveryHours);

        return new ModelValueSummaryDto
        {
            CostPerHour = deliveryHours == 0 ? 0 : facultyExpense / deliveryHours,
            NonCostPerEftsl = totalEftsl == 0 ? 0 : nonFacultyExpense / totalEftsl,
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
