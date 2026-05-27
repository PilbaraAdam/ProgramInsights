window.dialogHelpers = {
    open: (id) => {
        const el = document.getElementById(id);
        if (el && !el.open) el.showModal();
    },
    close: (id) => {
        const el = document.getElementById(id);
        if (el && el.open) el.close();
    }
};

window.fileHelpers = {
    download: (filename, base64) => {
        const bytes = Uint8Array.from(atob(base64), c => c.charCodeAt(0));
        const blob = new Blob([bytes], { type: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet' });
        const url = URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.href = url;
        a.download = filename;
        document.body.appendChild(a);
        a.click();
        document.body.removeChild(a);
        URL.revokeObjectURL(url);
    }
};

window.chartHelpers = (function () {
    const registry = {};

    function destroy(id) {
        if (registry[id]) {
            registry[id].destroy();
            delete registry[id];
        }
    }

    const AUD = new Intl.NumberFormat('en-AU', { style: 'currency', currency: 'AUD', maximumFractionDigits: 0 });
    const NUM = new Intl.NumberFormat('en-AU', { maximumFractionDigits: 2 });

    // Palette for cohort series
    const COHORT_COLORS = [
        '#3b82f6', '#8b5cf6', '#06b6d4', '#f59e0b',
        '#10b981', '#ec4899', '#6366f1', '#f97316'
    ];

    return {
        renderCostRevenue: function (canvasId, labels, facultyCosts, nonFacCosts, revenues) {
            destroy(canvasId);
            const ctx = document.getElementById(canvasId);
            if (!ctx) return;

            registry[canvasId] = new Chart(ctx, {
                data: {
                    labels,
                    datasets: [
                        {
                            type: 'bar',
                            label: 'Faculty Cost',
                            data: facultyCosts,
                            backgroundColor: '#ef4444cc',
                            stack: 'cost',
                            order: 2
                        },
                        {
                            type: 'bar',
                            label: 'Non-Faculty Cost',
                            data: nonFacCosts,
                            backgroundColor: '#f97316cc',
                            stack: 'cost',
                            order: 2
                        },
                        {
                            type: 'line',
                            label: 'Revenue',
                            data: revenues,
                            borderColor: '#22c55e',
                            backgroundColor: '#22c55e22',
                            borderWidth: 2.5,
                            pointRadius: 5,
                            pointBackgroundColor: '#22c55e',
                            tension: 0.3,
                            fill: false,
                            order: 1
                        }
                    ]
                },
                options: {
                    responsive: true,
                    maintainAspectRatio: false,
                    plugins: {
                        legend: { position: 'top' },
                        tooltip: {
                            callbacks: {
                                label: ctx => ` ${ctx.dataset.label}: ${AUD.format(ctx.parsed.y)}`
                            }
                        }
                    },
                    scales: {
                        x: { grid: { display: false } },
                        y: {
                            stacked: true,
                            ticks: { callback: v => AUD.format(v) }
                        }
                    }
                }
            });
        },

        renderEftslRampup: function (canvasId, labels, cohortDatasets) {
            destroy(canvasId);
            const ctx = document.getElementById(canvasId);
            if (!ctx) return;

            const datasets = cohortDatasets.map((d, i) => ({
                label: d.label,
                data: d.data,
                backgroundColor: COHORT_COLORS[i % COHORT_COLORS.length] + 'cc',
                stack: 'eftsl'
            }));

            registry[canvasId] = new Chart(ctx, {
                type: 'bar',
                data: { labels, datasets },
                options: {
                    responsive: true,
                    maintainAspectRatio: false,
                    plugins: {
                        legend: { position: 'top' },
                        tooltip: {
                            callbacks: {
                                label: ctx => ` ${ctx.dataset.label}: ${NUM.format(ctx.parsed.y)} EFTSL`
                            }
                        }
                    },
                    scales: {
                        x: { stacked: true, grid: { display: false } },
                        y: { stacked: true, ticks: { callback: v => NUM.format(v) } }
                    }
                }
            });
        },

        destroy
    };
})();
