// Synchroniczny skrypt w head: preferencja jest znana przed pierwszym rysowaniem.
(function () {
  var stored = null;
  try {
    stored = localStorage.getItem("dismode-motion");
  } catch (error) {
    stored = null;
  }
  var systemReduced = matchMedia("(prefers-reduced-motion: reduce)").matches;
  var reduced = systemReduced || stored === "reduced";
  document.documentElement.dataset.motion = reduced ? "reduced" : "full";
})();
