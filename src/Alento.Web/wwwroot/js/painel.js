// Alento · utilitários do painel (chamados via JS interop)
window.alento = {
    sair: function () { document.getElementById("form-sair")?.submit(); },
    copiar: async function (texto) {
        try { await navigator.clipboard.writeText(texto); return true; }
        catch {
            const t = document.createElement("textarea");
            t.value = texto; document.body.appendChild(t); t.select();
            const ok = document.execCommand("copy"); t.remove(); return ok;
        }
    },
    compartilhar: async function (titulo, texto, url) {
        if (navigator.share) { try { await navigator.share({ title: titulo, text: texto, url: url }); return true; } catch { return false; } }
        return false;
    },
    baixar: function (url) { window.location.href = url; },
    largura: function () { return window.innerWidth; },
    rolarPara: function (id) { document.getElementById(id)?.scrollIntoView({ behavior: "smooth", block: "center" }); }
};
