document.addEventListener("submit", event => {
    if (event.target.hasAttribute("data-download")) return;
    document.getElementById("progress").hidden = false;
    event.target.setAttribute("aria-busy", "true");
});
window.addEventListener("pageshow", () => {
    document.getElementById("progress").hidden = true;
    document.querySelectorAll("[aria-busy]").forEach(form => form.removeAttribute("aria-busy"));
});
