(function () {
    const page = document.querySelector('[data-cb-ai-robot]');
    if (!page) {
        return;
    }

    const recommendations = {
        btc: {
            symbol: 'BTCUSDT', direction: 'Long', directionTone: 'success', status: 'Aktif öneri', stateClass: 'is-active', confidence: '84%', confidenceValue: 84, setup: 'Trend Pulse', risk: 'Düşük', score: '09:42',
            summary: "Trend güçlü, momentum teyidi var ve hacim desteği setup'ı destekliyor.",
            reasons: ['Trend güçlü', 'Momentum uygun', 'Hacim desteği', 'Risk kabul edilebilir'],
            positives: 'EMA hizası, hacim ivmesi ve kısa vadeli momentum aynı yöne işaret ediyor.',
            caution: 'Makro haber akışı nedeniyle giriş boyutu placeholder olarak sınırlı tutulmalı.',
            note: 'Model notu: Bu setup korunmacı bir giriş penceresiyle düşünülmeli.',
            vetoBadge: ['Veto yok', 'success'],
            vetoSummary: 'Seçili setup için aktif bir veto bulunmuyor; AI öneri listesinde operasyonel olarak gösterilebilir.',
            vetoAction: 'Risk paneli ile birlikte değerlendir',
            drawerReason: 'Trend, momentum ve hacim aynı yönde hizalanıyor.',
            drawerVeto: 'Seçili öneri için aktif veto bulunmuyor.'
        },
        sol: {
            symbol: 'SOLUSDT', direction: 'Short', directionTone: 'danger', status: 'Aktif öneri', stateClass: 'is-active', confidence: '78%', confidenceValue: 78, setup: 'Breakdown Setup', risk: 'Orta', score: '09:40',
            summary: 'Trend zayıflığı, hacim artışı ve momentum kırılması short yönünü destekliyor.',
            reasons: ['Trend zayıf', 'Momentum aşağı kırıldı', 'Hacim teyidi', 'Risk sınır içinde'],
            positives: 'Yatay desteğin altına sarkma ve hacim sıkışmasının çözülmesi short yönü için olumlu.',
            caution: 'Volatilite yükselirse stop alanı genişletilmeli; risk motoru ile birlikte düşünülmeli.',
            note: 'Model notu: Short önerilerde futures açık değilse öneri yalnız watch statüsüne çekilebilir.',
            vetoBadge: ['Veto yok', 'success'],
            vetoSummary: 'Short setup uygun görünüyor; şu anda aktif veto sebebi yok.',
            vetoAction: 'Futures ve risk limiti ile birlikte değerlendir',
            drawerReason: 'Destek kırılımı ve hacim artışı short tarafında senaryoyu güçlendiriyor.',
            drawerVeto: 'Veto bulunmuyor; futures politikası uygunsa değerlendirilebilir.'
        },
        eth: {
            symbol: 'ETHUSDT', direction: 'Watch', directionTone: 'neutral', status: 'İzleme önerisi', stateClass: 'is-waiting', confidence: '63%', confidenceValue: 63, setup: 'Watch Setup', risk: 'Dengeli', score: '09:38',
            summary: 'Setup oluşuyor ancak giriş için gereken güven bandı tam tamamlanmadı.',
            reasons: ['Setup olgunlaşıyor', 'Trend nötr', 'Momentum artıyor', 'Ek teyit bekleniyor'],
            positives: 'Volatilite sakin, hacim tarafı toparlanıyor ve trend nötrden pozitife dönmeye aday.',
            caution: 'Giriş sinyali oluşmadan önce ek teyit ve confidence artışı beklenmeli.',
            note: 'Model notu: Watch statüsü, kullanıcının listede kalmasını ama işlem açmamasını önerir.',
            vetoBadge: ['Kısmi çekince', 'warning'],
            vetoSummary: 'Henüz aktif veto yok, ancak confidence bandı tam yeterli olmadığı için öneri watch statüsünde tutuluyor.',
            vetoAction: 'Bir sonraki skoru bekle',
            drawerReason: 'Kurulum yaklaşmış olsa da teyit zinciri tamamlanmadı.',
            drawerVeto: 'Watch statüsü nedeniyle işlem açma yerine izleme önerilir.'
        },
        avax: {
            symbol: 'AVAXUSDT', direction: 'Long', directionTone: 'success', status: 'Veto edildi', stateClass: 'is-low', confidence: '41%', confidenceValue: 41, setup: 'Veto adayı', risk: 'Yüksek', score: '09:35',
            summary: 'Momentum kırılgan, veri penceresi zayıf ve risk seviyesi kullanıcı limitiyle uyumlu görünmüyor.',
            reasons: ['Confidence düşük', 'Veri yetersiz', 'Risk yüksek', 'Piyasa belirsiz'],
            positives: 'İzlemeye değer bir yapı var ancak henüz güvenilir giriş kalitesine ulaşmıyor.',
            caution: 'Yüksek risk bandı ve eksik veri nedeniyle öneri veto edildi.',
            note: "Model notu: Veto paneli, AI'ın neden işlem açmadığını kullanıcıya açıklamak için hazırlanmıştır.",
            vetoBadge: ['Veto aktif', 'danger'],
            vetoSummary: 'Confidence düşük, veri yetersiz ve risk bandı yüksek olduğu için işlem açma önerisi verilmedi.',
            vetoAction: "Watchlist'e al ve daha sonra tekrar değerlendir",
            drawerReason: 'Pozitif kurulum izleri görülse de veri penceresi ve risk bandı yeterli değil.',
            drawerVeto: 'Confidence düşük, veri yetersiz ve kullanıcı risk limiti nedeniyle veto aktif.'
        }
    };

    const scenarios = {
        collecting: {
            badgeText: 'Veri topluyor', badgeClass: 'is-collecting', note: 'Model son veri penceresini topluyor; explainability alanı sınırlı, öneriler yalnızca izleme seviyesinde gösterilir.',
            version: 'AI-v0.9 foundation', lastScore: 'Henüz skor yok', lastData: 'Az önce', confidence: 'Sinyal bandı oluşuyor',
            healthBadge: ['Warming up', 'is-warming'], healthSummary: 'Model health özeti: veri penceresi doluyor, health bandı ve kalibrasyon aşaması devam ediyor.', healthBand: 'Kalibrasyon aşamasında', training: 'Bugün · 06:10',
            meta: { score: ['Henüz skor yok', 'Veri bekleniyor', 'neutral'], data: ['Az önce', 'Güncel', 'info'], refresh: ['Bugün · 07:20', 'Hazırlanıyor', 'neutral'], reco: ['Henüz yok', 'Beklemede', 'neutral'] },
            panel: 'list', low: false, stale: false
        },
        active: {
            badgeText: 'Aktif', badgeClass: 'is-active', note: 'AI öneri üretmeye hazır; explainability ve veto panelleri güncel skor penceresiyle birlikte görünür.',
            version: 'AI-v1.2 stable', lastScore: '09:42', lastData: '30 sn önce', confidence: 'Genel confidence bandı 74% · sağlıklı',
            healthBadge: ['Stable', 'is-stable'], healthSummary: 'Model health özeti: son skor ve veri penceresi güncel, scoring pipeline stabil görünüyor.', healthBand: '62% - 86%', training: 'Bugün · 05:45',
            meta: { score: ['Az önce', 'Güncel', 'success'], data: ['30 sn önce', 'Güncel', 'success'], refresh: ['Bugün · 07:20', 'Stable', 'info'], reco: ['09:42', 'Aktif', 'success'] },
            panel: 'list', low: false, stale: false
        },
        low: {
            badgeText: 'Düşük güven', badgeClass: 'is-low', note: 'Öneriler görünür, ancak confidence bandı düşük olduğu için veto ve uyarı alanları daha belirgin tutulur.',
            version: 'AI-v1.2 stable', lastScore: '09:39', lastData: '1 dk önce', confidence: 'Genel confidence bandı 54% · dikkat',
            healthBadge: ['Low confidence', 'is-low'], healthSummary: 'Model health özeti: veri akışı güncel, ancak öneri kalitesi risk filtresi tarafından daha sık baskılanıyor.', healthBand: '41% - 65%', training: 'Bugün · 05:45',
            meta: { score: ['1 dk önce', 'Düşük güven', 'warning'], data: ['1 dk önce', 'Güncel', 'info'], refresh: ['Bugün · 07:20', 'Stable', 'info'], reco: ['09:39', 'Kısıtlı', 'warning'] },
            panel: 'list', low: true, stale: false
        },
        stale: {
            badgeText: 'Degraded', badgeClass: 'is-degraded', note: 'Son skor eski olabilir; freshness warning ve model health paneli kullanıcıya verinin geciktiğini açık gösterir.',
            version: 'AI-v1.1 degraded', lastScore: '3 saat önce', lastData: '47 dk önce', confidence: 'Confidence bandı eski veriyle sınırlı',
            healthBadge: ['Degraded', 'is-degraded'], healthSummary: 'Model health özeti: skor zamanları gecikmiş, kullanıcı son önerilerin stale olabileceğini bilmelidir.', healthBand: 'Eski skor bandı', training: 'Dün · 22:10',
            meta: { score: ['3 saat önce', 'Stale', 'warning'], data: ['47 dk önce', 'Gecikmiş', 'warning'], refresh: ['Dün · 22:10', 'Eski', 'warning'], reco: ['2 saat önce', 'Stale', 'warning'] },
            panel: 'list', low: false, stale: true
        },
        empty: {
            badgeText: 'Beklemede', badgeClass: 'is-waiting', note: 'Uygun setup bulunmadığında öneri listesi boş kalır; kullanıcı yardım kartı ve refresh aksiyonu ile yönlendirilir.',
            version: 'AI-v1.2 stable', lastScore: '09:20', lastData: '1 dk önce', confidence: 'Uygun setup yok',
            healthBadge: ['Stable', 'is-stable'], healthSummary: 'Model health özeti: servis sağlıklı fakat şu an confidence bandına giren bir setup yok.', healthBand: '62% - 86%', training: 'Bugün · 05:45',
            meta: { score: ['22 dk önce', 'Güncel', 'info'], data: ['1 dk önce', 'Güncel', 'success'], refresh: ['Bugün · 07:20', 'Stable', 'info'], reco: ['Henüz yok', 'Beklemede', 'neutral'] },
            panel: 'empty', low: false, stale: false
        },
        loading: {
            badgeText: 'Beklemede', badgeClass: 'is-waiting', note: 'AI modülü verileri ve explainability yüzeyini hazırlıyor.',
            version: 'AI-v1.2 stable', lastScore: 'Hazırlanıyor', lastData: 'Az önce', confidence: 'AI yüzeyi hazırlanıyor',
            healthBadge: ['Collecting data', 'is-collecting'], healthSummary: 'Model health özeti: yüzey skeleton ile hazırlanıyor.', healthBand: 'Hazırlanıyor', training: 'Hazırlanıyor',
            meta: { score: ['Hazırlanıyor', 'Beklemede', 'neutral'], data: ['Az önce', 'Hazırlanıyor', 'info'], refresh: ['Hazırlanıyor', 'Beklemede', 'neutral'], reco: ['Hazırlanıyor', 'Beklemede', 'neutral'] },
            panel: 'loading', low: false, stale: false
        },
        error: {
            badgeText: 'Paused', badgeClass: 'is-paused', note: 'AI yüzeyi veri alamadığında kullanıcıya yeniden deneme ve yardım aksiyonlarıyla yön gösterir.',
            version: 'AI-v1.2 stable', lastScore: 'Alınamadı', lastData: 'Belirsiz', confidence: 'AI verisi alınamadı',
            healthBadge: ['Paused', 'is-paused'], healthSummary: 'Model health özeti: health veya scoring kaynağı geçici olarak ulaşılamaz olabilir.', healthBand: 'Belirsiz', training: 'Bilinmiyor',
            meta: { score: ['Alınamadı', 'Hata', 'danger'], data: ['Belirsiz', 'Kontrol et', 'warning'], refresh: ['Bilinmiyor', 'Pause', 'warning'], reco: ['Alınamadı', 'Hata', 'danger'] },
            panel: 'error', low: false, stale: true
        }
    };

    function text(id, value) {
        const el = document.getElementById(id);
        if (el) el.textContent = value;
    }

    function setBadgeTone(el, tone) {
        if (!el) return;
        el.classList.remove('cb-badge-success', 'cb-badge-warning', 'cb-badge-danger', 'cb-badge-info', 'cb-badge-neutral');
        el.classList.add('cb-badge-' + (tone || 'neutral'));
    }

    function setAiStateClass(el, className) {
        if (!el) return;
        el.classList.remove('is-active', 'is-waiting', 'is-collecting', 'is-low', 'is-degraded', 'is-paused', 'is-stable', 'is-warming');
        el.classList.add(className);
    }

    function setMeta(idPrefix, data) {
        text(idPrefix, data[0]);
        const badge = document.getElementById(idPrefix + '_badge');
        if (badge) {
            badge.textContent = data[1];
            setBadgeTone(badge, data[2]);
        }
    }

    function selectRecommendation(key) {
        const item = recommendations[key];
        if (!item) return;

        page.querySelectorAll('[data-cb-ai-row]').forEach(function (row) {
            row.classList.toggle('is-selected', row.getAttribute('data-cb-ai-id') === key);
        });

        text('cb_ai_explain_symbol', item.symbol + ' · ' + item.direction);
        text('cb_ai_explain_summary', item.summary);
        text('cb_ai_positive_signals', item.positives);
        text('cb_ai_caution_text', item.caution);
        text('cb_ai_model_note', item.note);

        const reasonWrap = document.getElementById('cb_ai_explain_reasons');
        if (reasonWrap) {
            reasonWrap.innerHTML = item.reasons.map(function (reason, index) {
                const tone = index === 0 ? 'success' : 'info';
                return '<span class="cb-badge cb-badge-' + tone + '">' + reason + '</span>';
            }).join('');
        }

        const vetoBadge = document.getElementById('cb_ai_veto_badge');
        if (vetoBadge) {
            vetoBadge.textContent = item.vetoBadge[0];
            setBadgeTone(vetoBadge, item.vetoBadge[1]);
        }
        text('cb_ai_veto_summary', item.vetoSummary);
        text('cb_ai_veto_action', item.vetoAction);
        document.getElementById('cb_ai_veto_warning')?.classList.toggle('d-none', item.vetoBadge[1] !== 'danger' && item.vetoBadge[1] !== 'warning');

        const vetoList = document.getElementById('cb_ai_veto_list');
        if (vetoList) {
            const tone = item.vetoBadge[1] === 'success' ? 'success' : item.vetoBadge[1];
            const title = item.vetoBadge[1] === 'success' ? 'Öneri geçerli' : 'Veto gerekçesi';
            vetoList.innerHTML = '<div class="cb-rule-setting-item"><div class="cb-rule-setting-header"><strong>' + title + '</strong><span class="cb-badge cb-badge-' + tone + '">' + item.vetoBadge[0] + '</span></div><div class="text-muted font-size-sm mt-2">' + item.vetoSummary + '</div></div>';
        }

        text('cb_ai_drawer_title', item.symbol + ' · ' + item.direction);
        text('cb_ai_drawer_direction', item.direction);
        text('cb_ai_drawer_confidence', item.confidence);
        text('cb_ai_drawer_risk', item.risk);
        text('cb_ai_drawer_score', item.score);
        text('cb_ai_drawer_reason', item.drawerReason);
        text('cb_ai_drawer_veto', item.drawerVeto);
        text('cb_ai_drawer_note', item.note);
        text('cb_ai_drawer_state', item.status);
        setAiStateClass(document.getElementById('cb_ai_drawer_state'), item.stateClass);

        const drawerVetoBadge = document.getElementById('cb_ai_drawer_veto_badge');
        if (drawerVetoBadge) {
            drawerVetoBadge.textContent = item.vetoBadge[0];
            setBadgeTone(drawerVetoBadge, item.vetoBadge[1]);
        }
    }

    function applyFilter(filter) {
        page.querySelectorAll('[data-cb-ai-filter]').forEach(function (chip) {
            chip.classList.toggle('is-active', chip.getAttribute('data-cb-ai-filter') === filter);
        });

        page.querySelectorAll('[data-cb-ai-row]').forEach(function (row) {
            const direction = row.getAttribute('data-cb-ai-direction');
            const confidence = Number(row.getAttribute('data-cb-ai-confidence') || '0');
            const veto = row.getAttribute('data-cb-ai-veto') === 'true';
            const visible = filter === 'all'
                || (filter === 'high' && confidence >= 75)
                || (filter === 'veto' && veto)
                || filter === direction;
            row.classList.toggle('d-none', !visible);
        });
    }

    function setScenario(name) {
        const scenario = scenarios[name];
        if (!scenario) return;

        page.setAttribute('data-cb-ai-scenario', name);
        page.querySelectorAll('[data-cb-ai-scenario-trigger]').forEach(function (chip) {
            chip.classList.toggle('is-active', chip.getAttribute('data-cb-ai-scenario-trigger') === name);
        });

        text('cb_ai_state_badge', scenario.badgeText);
        setAiStateClass(document.getElementById('cb_ai_state_badge'), scenario.badgeClass);
        text('cb_ai_operational_note', scenario.note);
        text('cb_ai_model_version', scenario.version);
        text('cb_ai_last_score', scenario.lastScore);
        text('cb_ai_last_data', scenario.lastData);
        text('cb_ai_confidence_summary', scenario.confidence);

        text('cb_ai_health_version', scenario.version);
        text('cb_ai_health_score', scenario.lastScore);
        text('cb_ai_health_training', scenario.training);
        text('cb_ai_health_band', scenario.healthBand);
        text('cb_ai_health_summary', scenario.healthSummary);
        text('cb_ai_health_badge', scenario.healthBadge[0]);
        setAiStateClass(document.getElementById('cb_ai_health_badge'), scenario.healthBadge[1]);

        setMeta('cb_ai_meta_score', scenario.meta.score);
        setMeta('cb_ai_meta_data', scenario.meta.data);
        setMeta('cb_ai_meta_refresh', scenario.meta.refresh);
        setMeta('cb_ai_meta_reco', scenario.meta.reco);

        page.querySelectorAll('[data-cb-ai-panel]').forEach(function (panel) {
            panel.classList.toggle('d-none', panel.getAttribute('data-cb-ai-panel') !== scenario.panel);
        });

        document.querySelectorAll('.cb-ai-warning-surface').forEach(function (el) {
            el.classList.toggle('d-none', !(scenario.low || scenario.stale));
        });
    }

    function setButtonLoading(button, isLoading) {
        if (!button) return;
        button.classList.toggle('is-loading', isLoading);
        button.toggleAttribute('disabled', isLoading);
    }

    document.addEventListener('click', function (event) {
        const scenarioTrigger = event.target.closest('[data-cb-ai-scenario-trigger]');
        if (scenarioTrigger) {
            event.preventDefault();
            setScenario(scenarioTrigger.getAttribute('data-cb-ai-scenario-trigger'));
        }

        const filterTrigger = event.target.closest('[data-cb-ai-filter]');
        if (filterTrigger) {
            event.preventDefault();
            applyFilter(filterTrigger.getAttribute('data-cb-ai-filter'));
        }

        const selectTrigger = event.target.closest('[data-cb-ai-select],[data-cb-ai-detail]');
        if (selectTrigger) {
            const key = selectTrigger.getAttribute('data-cb-ai-select') || selectTrigger.getAttribute('data-cb-ai-detail');
            selectRecommendation(key);
        }

        const refreshTrigger = event.target.closest('[data-cb-ai-refresh]');
        if (refreshTrigger) {
            event.preventDefault();
            setButtonLoading(refreshTrigger, true);
            window.setTimeout(function () {
                setButtonLoading(refreshTrigger, false);
            }, 700);
        }
    });

    setScenario('collecting');
    applyFilter('all');
    selectRecommendation('btc');
})();
