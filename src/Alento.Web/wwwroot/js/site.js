// Alento · interações do site público (sem dependências)
(function () {
    "use strict";
    var moeda = new Intl.NumberFormat("pt-BR", { style: "currency", currency: "BRL", maximumFractionDigits: 0 });
    var PRECO = 79;

    // Calculadora de perda com faltas (ancoragem: 1 falta evitada > mensalidade)
    var calc = document.getElementById("calc");
    if (calc) {
        var v = document.getElementById("calc-valor");
        var f = document.getElementById("calc-faltas");
        var h = document.getElementById("calc-horas");
        var atualizar = function () {
            var valor = +v.value, faltas = +f.value, horas = +h.value;
            var mes = valor * faltas;
            document.getElementById("out-valor").textContent = moeda.format(valor);
            document.getElementById("out-faltas").textContent = faltas;
            document.getElementById("out-horas").textContent = horas + " h";
            document.getElementById("res-perda").textContent = moeda.format(mes) + "/mês";
            document.getElementById("res-ano").textContent =
                moeda.format(mes * 12) + " por ano, mais " + (horas * 52) + " horas de trabalho administrativo.";
            var vezes = (valor / PRECO).toLocaleString("pt-BR", { maximumFractionDigits: 1 });
            document.getElementById("res-veredito").innerHTML =
                "Se o Alento evitar <b>uma única falta</b>, ele já se paga <b>" + vezes + " vezes</b> no mês.";
        };
        [v, f, h].forEach(function (el) { el.addEventListener("input", atualizar); });
        atualizar();
    }

    // Borda na navegação ao rolar + CTA fixo no celular depois do hero
    var nav = document.querySelector(".nav");
    var fixo = document.getElementById("cta-fixo");
    var hero = document.querySelector(".hero");
    var final = document.querySelector(".cta-final");
    var aoRolar = function () {
        if (nav) nav.classList.toggle("rolou", window.scrollY > 8);
        if (fixo && hero) {
            var passouHero = hero.getBoundingClientRect().bottom < 0;
            var noFinal = final && final.getBoundingClientRect().top < window.innerHeight;
            var mostrar = passouHero && !noFinal;
            fixo.classList.toggle("visivel", mostrar);
            fixo.setAttribute("aria-hidden", mostrar ? "false" : "true");
        }
    };
    window.addEventListener("scroll", aoRolar, { passive: true });
    aoRolar();

    // Revelar seções ao entrar na tela
    var itens = document.querySelectorAll(".revelar");
    if ("IntersectionObserver" in window) {
        var io = new IntersectionObserver(function (entradas) {
            entradas.forEach(function (e) {
                if (e.isIntersecting) { e.target.classList.add("visivel"); io.unobserve(e.target); }
            });
        }, { threshold: 0.12, rootMargin: "0px 0px -40px 0px" });
        itens.forEach(function (el) { io.observe(el); });
    } else {
        itens.forEach(function (el) { el.classList.add("visivel"); });
    }

    // Preserva UTMs dos anúncios até o cadastro (atribuição de cada CTA)
    var params = new URLSearchParams(location.search);
    var utm = new URLSearchParams();
    params.forEach(function (valor, chave) { if (/^utm_|^gclid$|^fbclid$/.test(chave)) utm.set(chave, valor); });
    try {
        if ([...utm.keys()].length) sessionStorage.setItem("alento_utm", utm.toString());
        else if (sessionStorage.getItem("alento_utm")) utm = new URLSearchParams(sessionStorage.getItem("alento_utm"));
    } catch (e) { /* armazenamento indisponível */ }
    document.querySelectorAll("a[data-cta]").forEach(function (a) {
        var url = new URL(a.getAttribute("href"), location.origin);
        utm.forEach(function (valor, chave) { url.searchParams.set(chave, valor); });
        url.searchParams.set("origem", a.getAttribute("data-cta"));
        a.setAttribute("href", url.pathname + url.search);
    });
})();
