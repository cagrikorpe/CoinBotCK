$(document).ready(function () {
    $(document).on("click", ".refresh", function (e) {
        e.preventDefault();
        $("#overlay").show();
        $.ajax({
            url: "/Main/_List",
            type: "GET",
            success: function (data) {
                $("#partialContainer").html(data);
            },
            error: function () {
                alert("Veri yüklenirken bir hata oluştu!");
            },
            complete: function () {
                let randomDelay = Math.floor(Math.random() * (90000 - 10000 + 1)) + 10000;
                setTimeout(function () {
                    $("#overlay").hide();
                    $(".refresh").css("position", "static").css("left", "0");
                }, randomDelay);
            }
        });
    });
});
