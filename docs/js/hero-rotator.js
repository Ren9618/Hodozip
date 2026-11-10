/* ==========================================================================
   Hodozip 公式サイト: ヒーローの画像ローテーション
   - 外部ライブラリ / CDN 不使用。素の JavaScript。
   - 4 秒ごとにフェードで次の画像へ切り替える。
   - ホバー中・フォーカス中は停止し、外れると再開する。
   - ドットで手動切り替えできる（切り替えたらタイマーをリセット）。
   - prefers-reduced-motion では自動切り替えしない（手動切り替えのみ可）。
   ========================================================================== */
(function () {
  "use strict";

  var INTERVAL_MS = 4000;

  function setup(rotator) {
    var slides = rotator.querySelectorAll(".hero-rot-slide");
    var dots = rotator.querySelectorAll(".hero-rot-dot");
    var caption = rotator.querySelector(".hero-rot-caption");
    var count = slides.length;
    if (count < 1) {
      return;
    }

    var current = 0;
    for (var i = 0; i < count; i++) {
      if (slides[i].classList.contains("is-active")) {
        current = i;
        break;
      }
    }

    var timer = null;
    var hovering = false;
    var focusing = false;
    var reduced = window.matchMedia("(prefers-reduced-motion: reduce)");

    function apply(index) {
      current = index;
      for (var s = 0; s < count; s++) {
        var active = s === index;
        slides[s].classList.toggle("is-active", active);
        slides[s].setAttribute("aria-hidden", active ? "false" : "true");
      }
      for (var d = 0; d < dots.length; d++) {
        var dotActive = d === index;
        dots[d].classList.toggle("is-active", dotActive);
        if (dotActive) {
          dots[d].setAttribute("aria-current", "true");
        } else {
          dots[d].removeAttribute("aria-current");
        }
      }
      var text = slides[index].getAttribute("data-caption");
      if (caption && text) {
        caption.textContent = text;
      }
    }

    function stop() {
      if (timer !== null) {
        clearInterval(timer);
        timer = null;
      }
    }

    function start() {
      stop();
      if (reduced.matches || hovering || focusing || document.hidden) {
        return;
      }
      timer = setInterval(function () {
        apply((current + 1) % count);
      }, INTERVAL_MS);
    }

    /* 手動切り替え: 表示を更新し、タイマーをリセットして再開 */
    function goTo(index) {
      apply(index % count);
      start();
    }

    for (i = 0; i < dots.length; i++) {
      (function (dot, index) {
        dot.addEventListener("click", function (event) {
          goTo(index);
          /* マウスでクリックしたときは、ボタンに残るフォーカスで
             自動切り替えが止まり続けないようにフォーカスを外す
             （キーボード操作時はフォーカスを維持する） */
          if (event.detail > 0 && typeof dot.blur === "function") {
            dot.blur();
          }
        });
      })(dots[i], i);
    }

    /* ホバー・フォーカスで停止 */
    rotator.addEventListener("mouseenter", function () {
      hovering = true;
      stop();
    });
    rotator.addEventListener("mouseleave", function () {
      hovering = false;
      start();
    });
    rotator.addEventListener("focusin", function () {
      focusing = true;
      stop();
    });
    rotator.addEventListener("focusout", function () {
      focusing = false;
      start();
    });

    /* 共存できない動きはやめる / 動きの設定の変更にも追従 */
    if (typeof reduced.addEventListener === "function") {
      reduced.addEventListener("change", function () {
        if (reduced.matches) {
          stop();
        } else {
          start();
        }
      });
    }

    /* タブが見えていない間は回さない */
    document.addEventListener("visibilitychange", function () {
      if (document.hidden) {
        stop();
      } else {
        start();
      }
    });

    apply(current);
    start();
  }

  var rotators = document.querySelectorAll(".hero-rot");
  for (var r = 0; r < rotators.length; r++) {
    setup(rotators[r]);
  }
})();
