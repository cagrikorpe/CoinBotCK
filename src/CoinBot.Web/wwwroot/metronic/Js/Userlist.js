var _get = "/Main/GetUser";
var _update = "/Main/UptUser";
var _Soupdate = "/Main/PasswordReset";
var _DeleteUser = "/Main/DeleteUser";
var _IsActive = "/Main/IsActive";

function DeleteUser(itm) {
    var asd = $(itm).attr("data-id");
    alert(asd);
    swal.fire({
        title: 'Kullanıcıyı disable İşlemi yaparken kullanıcının üye olduğu gruplardan çıkartılacağını ve bulunduğu OU dan taşınacağını belirtmek İsterim, yinede bu İşlemi yapmak İstermisin?',
        text: "Bu işlemi geri alamayacaksın!",
        type: 'warning',
        showCancelButton: true,
        confirmButtonText: 'Evet!',
        cancelButtonText: 'Hayır!',
        reverseButtons: true
    }).then(function (result) {
        if (result.value) {
            $.post(_DeleteUser + "?" + Date, { "Id": asd }, function (a, b, c) {
                toastr.options = {
                    "closeButton": false,
                    "debug": false,
                    "newestOnTop": false,
                    "progressBar": false,
                    "positionClass": "toast-top-right",
                    "preventDuplicates": false,
                    "onclick": null,
                    "showDuration": "300",
                    "hideDuration": "1000",
                    "timeOut": "5000",
                    "extendedTimeOut": "1000",
                    "showEasing": "swing",
                    "hideEasing": "linear",
                    "showMethod": "fadeIn",
                    "hideMethod": "fadeOut"
                };
                toastr.success("Kullanıcı Disable Edilmiştir....");
                setTimeout(function () {
                    location.reload();
                }, 500);
                //location.reload();
            });

        }
    });
}

function IsActive(itm) {
    var asd = $(itm).attr("data-id");
    //alert(asd);
    swal.fire({
        title: 'Kullanıcıyı durumu değiştirilecektir!',
        text: "Bu işlemi yapmak istediğinize eminmisiniz?",
        type: 'warning',
        showCancelButton: true,
        confirmButtonText: 'Evet!',
        cancelButtonText: 'Hayır!',
        reverseButtons: true
    }).then(function (result) {
        if (result.value) {
            $.post(_IsActive + "?" + Date, { "Id": asd }, function (a, b, c) {
                toastr.options = {
                    "closeButton": false,
                    "debug": false,
                    "newestOnTop": false,
                    "progressBar": false,
                    "positionClass": "toast-top-right",
                    "preventDuplicates": false,
                    "onclick": null,
                    "showDuration": "300",
                    "hideDuration": "1000",
                    "timeOut": "5000",
                    "extendedTimeOut": "1000",
                    "showEasing": "swing",
                    "hideEasing": "linear",
                    "showMethod": "fadeIn",
                    "hideMethod": "fadeOut"
                };
                toastr.success("Kullanıcı Disable Edilmiştir....");
                setTimeout(function () {
                    location.reload();
                }, 500);
                //location.reload();
            });

        }
    });
}


$("#Passwd").click(function () {
    document.getElementById("rndm").style.display = "none"
    document.getElementById("pswd").style.display = "block"
});
$("#Passrnd").click(function () {
    document.getElementById("pswd").style.display = "none"
    document.getElementById("rndm").style.display = "block"
});

$(".SendPaswd").off("click").on("click", function () {
    try {
        try {
            var jsonData = {
                Samaccountname: $(".PSamaccountname").val(),
                length: $(".Plength").val(),
                sym: $(".Psym")[0].checked,
                upper: $(".Pupper")[0].checked,
                lower: $(".Plower")[0].checked,
                nmb: $(".Pnmb")[0].checked,
                newpassword: $(".Pnewpassword").val(),
                randompw: document.getElementById("Passrnd").checked,
                Sendmail: $(".PSendmail")[0].checked,
                Sendmobile: $(".PSendmobile")[0].checked,
            };
            $.post(_Soupdate + "?" + Date, jsonData, function (data, status, xhr) {
                if (data =="true") {
                    toastr.success("Şifre Resetleme İşlemi Tamamlanmıştır");
                    setTimeout(function () {
                        location.reload();
                    }, 1000);
                    location.reload();
                } else {

                    toastr.error("Şifre resetleme işlemi tamamlanamamıştır");
                }
                //alert(toastr.success);
                //$(".photnm").val(""),
                //    $(".numtnm").val("")
                
            });
        } catch (e) {
            console.log(e);
        }
    } catch (e) {
        console.log(e);
    }
});

function UpdatePassword(itm) {
    var asd = $(itm).attr("data-id");
    document.getElementById("Samaccountname-input").value = asd;  
}


function UpdateUser(item) {
    $(".usrcgivenName").val("");
    $(".usrcSamaccountname").val("");
    $(".usrcsn").val("");
    $(".usrcdisplayName").val("");
    $(".usrcphysicalDeliveryOfficeName").val("");
    $(".usrcmail").val("");
    $(".usrcmobile").val("");
    $(".usrctitle").val("");
    $(".usrcdepartment").val("");
    $("#exampleTextarea").text("");
    var itemId = $(item).attr("data-id");
    $.post(_get + "?" + Date, { "itemId": itemId }, function (data, status, xhr) {

        //$(".Id").val(data.Id);
        //$(".name").val(data.Name);


        $(".usrcgivenName").val(data.IsName);
        $(".usrcSamaccountname").val(data.Samaccountname);
        $(".usrcsn").val(data.IsSurname);
        $(".usrcdisplayName").val(data.DisplayName);
        $(".usrcphysicalDeliveryOfficeName").val(data.Office);
        $(".usrcmail").val(data.EmailAddress);
        $(".usrcmobile").val(data.Mobile);
        $(".usrctitle").val(data.Title);
        $(".usrcdepartment").val(data.department);
        $("#exampleTextarea").text(data.Description);
    }).fail(function () {
        toastr.error("Kayıt İşlemi Başarısız2", "İşlem");
    });
}



function Guncelle(item) {
    var usermodel = {
        IsName: $(".usrcgivenName").val(),
        Samaccountname: $(".usrcSamaccountname").val(),
        IsSurname: $(".usrcsn").val(),
        DisplayName: $(".usrcdisplayName").val(),
        Office: $(".usrcphysicalDeliveryOfficeName").val(),
        EmailAddress: $(".usrcmail").val(),
        Mobile: $(".usrcmobile").val(),
        Title: $(".usrctitle").val(),
        department: $(".usrcdepartment").val(),
        Description: document.getElementById("exampleTextarea").value
    };
    $.post(_update + "?" + Date, usermodel, function (data, status, xhr) {
        toastr.success("Kayıt İşlemi Başarılı", "İşlem");
        modal.modal("hide");
        setTimeout(
            function () {
                location.reload(true);
            }, 150);


    }).fail(function () {
        toastr.error("Kayıt İşlemi Başarısız", "İşlem");
    });

}